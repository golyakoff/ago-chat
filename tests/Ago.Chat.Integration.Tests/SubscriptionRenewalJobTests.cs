using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.CreateCheckoutSession;
using Ago.Chat.Application.UseCases.OperatorRoleSeats;
using Ago.Chat.Application.UseCases.ProcessSubscriptionRenewal;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Modules;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Infrastructure.YooKassa;
using Ago.Chat.Worker;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Npgsql;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `13-03`: <see cref="SubscriptionRenewalJob"/>'s own real Postgres, real domain path
/// (<see cref="ProcessSubscriptionRenewalHandler"/> -&gt; <see cref="ISubscriptionRenewalApplier"/> ->
/// <c>Site.ActivateSubscription</c>/<c>BillingSubscription</c>) end to end, against the same fake-ЮKassa
/// -Kestrel-host technique <c>YooKassaPaymentsApiClientTests</c> already established for the outbound
/// half of this integration - reused here rather than a second hand-rolled HTTP double for the same
/// third party (this item's own brief).
///
/// <para>Every 7-day/1-day boundary below is proven by moving a <see cref="FixedClock"/> forward
/// between calls to <see cref="SubscriptionRenewalJob.RunOnceAsync"/>, never by sleeping or by counting
/// retries - a test that could not tell "7 days passed" from "7 ticks happened" would prove the wrong
/// thing (this item's own brief).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SubscriptionRenewalJobTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    // `25-43`: the seat-pricing figures this whole file's own assertions are computed against - no
    // longer a BillingOptions instance, but the same two Rouble numbers, now published into the real
    // Postgres price catalog by SeedCurrentSeatPricesAsync at the top of every seeding helper below.
    private const decimal BaseSeatPriceRub = 500m;
    private const decimal PricePerExtraSeatRub = 100m;

    [Fact]
    public async Task RunOnceAsync_WhenTheRechargeIsDeclined_EntersPastDue_AndLeavesSiteEntitlementsUnchanged()
    {
        var (siteId, subscriptionId) = await SeedSucceededSubscriptionAsync(seats: 5, tier: SubscriptionTierBands.Starter, periodEnd: Now);

        await using var host = await BuildFakeYooKassaHostAsync(app =>
            app.MapPost("payments", () => Results.Json(
                new { code = "invalid_request", description = "card declined" }, statusCode: StatusCodes.Status400BadRequest)));

        await CreateJob(host.BaseUrl, new FixedClock(Now)).RunOnceAsync(CancellationToken.None);

        await using var verify = fixture.CreateDbContext();
        var subscription = await verify.BillingSubscriptions.SingleAsync(s => s.Id == subscriptionId);
        Assert.Equal(BillingSubscriptionStatus.PastDue, subscription.Status);
        Assert.Equal(Now, subscription.PastDueSince);

        var site = await verify.Sites.SingleAsync(s => s.Id == siteId);
        Assert.Equal(SubscriptionTierBands.Starter, site.Tier);
        Assert.Equal(5, site.SeatLimit);
    }

    [Fact]
    public async Task RunOnceAsync_WhenAPastDueRetrySucceedsWithinTheWindow_ClearsBackToSucceeded()
    {
        var (siteId, subscriptionId) = await SeedSucceededSubscriptionAsync(seats: 5, tier: SubscriptionTierBands.Starter, periodEnd: Now);
        await MarkPastDueAsync(subscriptionId, Now);

        var retryTime = Now + TimeSpan.FromDays(1);
        await using var host = await BuildFakeYooKassaHostAsync(app =>
            app.MapPost("payments", () => Results.Json(new { id = "pmt_retry_ok", status = "succeeded" })));

        await CreateJob(host.BaseUrl, new FixedClock(retryTime)).RunOnceAsync(CancellationToken.None);

        await using var verify = fixture.CreateDbContext();
        var subscription = await verify.BillingSubscriptions.SingleAsync(s => s.Id == subscriptionId);
        Assert.Equal(BillingSubscriptionStatus.Succeeded, subscription.Status);
        Assert.Null(subscription.PastDueSince);
        Assert.Equal(Now + BillingSubscription.PeriodLength, subscription.CurrentPeriodEnd);

        var site = await verify.Sites.SingleAsync(s => s.Id == siteId);
        Assert.Equal(SubscriptionTierBands.Starter, site.Tier);
        Assert.Equal(5, site.SeatLimit);
    }

    [Fact]
    public async Task RunOnceAsync_WhenTheSevenDayRetryWindowCloses_DowngradesTheSiteToFree_WithoutAttemptingAFinalCharge()
    {
        var (siteId, subscriptionId) = await SeedSucceededSubscriptionAsync(seats: 5, tier: SubscriptionTierBands.Starter, periodEnd: Now);
        await MarkPastDueAsync(subscriptionId, Now);

        var chargeAttempts = 0;
        await using var host = await BuildFakeYooKassaHostAsync(app =>
            app.MapPost("payments", () =>
            {
                Interlocked.Increment(ref chargeAttempts);
                return Results.Json(new { code = "invalid_request", description = "still declined" }, statusCode: StatusCodes.Status400BadRequest);
            }));

        var pastWindow = Now + BillingSubscription.PastDueRetryWindow;
        await CreateJob(host.BaseUrl, new FixedClock(pastWindow)).RunOnceAsync(CancellationToken.None);

        await using var verify = fixture.CreateDbContext();
        var subscription = await verify.BillingSubscriptions.SingleAsync(s => s.Id == subscriptionId);
        Assert.Equal(BillingSubscriptionStatus.Lapsed, subscription.Status);

        var site = await verify.Sites.SingleAsync(s => s.Id == siteId);
        Assert.Equal("free", site.Tier);
        Assert.Equal(1, site.SeatLimit);

        // `decisions/0006`'s own "no charge attempt, successful or otherwise" applies just as much to
        // the tick that finally gives up as to a cancellation - proven, not assumed from the handler's
        // own branch order.
        Assert.Equal(0, chargeAttempts);
    }

    [Fact]
    public async Task RunOnceAsync_WhenCancelledAndDueAtPeriodEnd_LapsesWithoutEverReachingTheFakeYooKassaHost()
    {
        var (siteId, subscriptionId) = await SeedSucceededSubscriptionAsync(seats: 5, tier: SubscriptionTierBands.Starter, periodEnd: Now);
        await RequestCancellationAsync(subscriptionId, Now - TimeSpan.FromDays(1));

        var chargeAttempts = 0;
        await using var host = await BuildFakeYooKassaHostAsync(app =>
            app.MapPost("payments", () =>
            {
                Interlocked.Increment(ref chargeAttempts);
                return Results.Json(new { id = "pmt_should_not_happen", status = "succeeded" });
            }));

        await CreateJob(host.BaseUrl, new FixedClock(Now)).RunOnceAsync(CancellationToken.None);

        await using var verify = fixture.CreateDbContext();
        var subscription = await verify.BillingSubscriptions.SingleAsync(s => s.Id == subscriptionId);
        Assert.Equal(BillingSubscriptionStatus.Lapsed, subscription.Status);

        var site = await verify.Sites.SingleAsync(s => s.Id == siteId);
        Assert.Equal("free", site.Tier);
        Assert.Equal(1, site.SeatLimit);
        Assert.Equal(0, chargeAttempts);
    }

    [Fact]
    public async Task RunOnceAsync_WhenNotCancelled_KeepsThePaidTierRunningUntilPeriodEnd()
    {
        // A cancelled-but-not-yet-expired subscription: the period end has not passed, so the job has
        // nothing due for this row at all - proven by the site's own tier staying paid, not merely by
        // the subscription row's own CancelRequested flag looking right.
        var (siteId, subscriptionId) = await SeedSucceededSubscriptionAsync(
            seats: 5, tier: SubscriptionTierBands.Starter, periodEnd: Now + TimeSpan.FromDays(10));
        await RequestCancellationAsync(subscriptionId, Now);

        await using var host = await BuildFakeYooKassaHostAsync(app =>
            app.MapPost("payments", () => Results.Json(new { id = "pmt_x", status = "succeeded" })));

        await CreateJob(host.BaseUrl, new FixedClock(Now)).RunOnceAsync(CancellationToken.None);

        await using var verify = fixture.CreateDbContext();
        var site = await verify.Sites.SingleAsync(s => s.Id == siteId);
        Assert.Equal(SubscriptionTierBands.Starter, site.Tier);
        Assert.Equal(5, site.SeatLimit);
    }

    [Fact]
    public async Task RunOnceAsync_WhenARenewalSucceedsWithAPendingDowngrade_AppliesItAndUpdatesTheSite()
    {
        var (siteId, subscriptionId) = await SeedSucceededSubscriptionAsync(seats: 20, tier: SubscriptionTierBands.Growth, periodEnd: Now);
        await ScheduleSeatDecreaseAsync(subscriptionId, newSeatCount: 5, newTier: SubscriptionTierBands.Starter);

        await using var host = await BuildFakeYooKassaHostAsync(app =>
            app.MapPost("payments", () => Results.Json(new { id = "pmt_renew_ok", status = "succeeded" })));

        await CreateJob(host.BaseUrl, new FixedClock(Now)).RunOnceAsync(CancellationToken.None);

        await using var verify = fixture.CreateDbContext();
        var subscription = await verify.BillingSubscriptions.SingleAsync(s => s.Id == subscriptionId);
        Assert.Equal(5, subscription.RequestedSeats);
        Assert.Equal(SubscriptionTierBands.Starter, subscription.Tier);
        Assert.Null(subscription.PendingSeatCount);

        var site = await verify.Sites.SingleAsync(s => s.Id == siteId);
        Assert.Equal(SubscriptionTierBands.Starter, site.Tier);
        Assert.Equal(5, site.SeatLimit);
    }

    // `23-86`/`adr/0159`: an option's own renewal grants its entitlement, its own lapse revokes it -
    // proven directly against SubscriptionRenewalApplier rather than through the whole job/handler
    // pipeline, since ProcessSubscriptionRenewalHandler deliberately refuses to compute a recurring
    // charge amount for an option (no price source exists in this item's scope - see that handler's
    // own remarks). The applier is exactly where this item's brief places the requirement ("same
    // transaction as the existing applier writes"), so it is exactly what these tests exercise.

    [Fact]
    public async Task ApplyRenewalSuccessAsync_ForAnOptionSubscription_GrantsItsEntitlement_AndLeavesTheSiteUntouched()
    {
        var (siteId, _) = await SeedSucceededSubscriptionAsync(seats: 5, tier: SubscriptionTierBands.Starter, periodEnd: Now + TimeSpan.FromDays(20));
        var optionId = await SeedDueOptionSubscriptionAsync(siteId, new BillingOptionKey("channel-telegram"), periodEnd: Now);

        await using var db = fixture.CreateDbContext();
        var applier = BuildApplier(db, new Dictionary<string, string?> { ["channel-telegram"] = "channel" });

        await applier.ApplyRenewalSuccessAsync(optionId, Now, 0, 0, [], CancellationToken.None);

        await using var verify = fixture.CreateDbContext();
        var option = await verify.BillingSubscriptions.SingleAsync(s => s.Id == optionId);
        Assert.Equal(BillingSubscriptionStatus.Succeeded, option.Status);
        Assert.Equal(Now + BillingSubscription.PeriodLength, option.CurrentPeriodEnd);

        var grant = await verify.ModuleQuantityGrants.SingleAsync(g => g.SiteId == siteId && g.ModuleKey == new ModuleKey("channel"));
        Assert.Equal(1, grant.Quantity);

        var outboxRow = await verify.Set<OutboxMessage>().SingleAsync(
            o => o.Type == nameof(ModuleQuantityGranted) && o.PartitionKey == siteId.Value.ToString());
        var contract = System.Text.Json.JsonSerializer.Deserialize<ModuleQuantityGranted>(outboxRow.Payload)!;
        Assert.Equal("channel", contract.ModuleKey);
        Assert.Equal(1, contract.Quantity);

        // The site's own Tier/SeatLimit are the base subscription's business alone - an option's own
        // renewal must never touch them.
        var site = await verify.Sites.SingleAsync(s => s.Id == siteId);
        Assert.Equal(SubscriptionTierBands.Starter, site.Tier);
        Assert.Equal(5, site.SeatLimit);
    }

    [Fact]
    public async Task ApplyLapseAsync_ForAnOptionSubscription_RevokesItsEntitlement_AndLeavesTheBaseSiteUntouched()
    {
        var (siteId, _) = await SeedSucceededSubscriptionAsync(seats: 5, tier: SubscriptionTierBands.Starter, periodEnd: Now + TimeSpan.FromDays(20));
        var optionId = await SeedDueOptionSubscriptionAsync(siteId, new BillingOptionKey("channel-telegram"), periodEnd: Now);
        var mappings = new Dictionary<string, string?> { ["channel-telegram"] = "channel" };

        await using (var db = fixture.CreateDbContext())
        {
            // First renewal grants it - a lapse must find something real to take away, not merely
            // exercise the revoke path against a row that was never granted.
            await BuildApplier(db, mappings).ApplyRenewalSuccessAsync(optionId, Now, 0, 0, [], CancellationToken.None);
        }

        await MarkPastDueAsync(optionId, Now);

        await using var db2 = fixture.CreateDbContext();
        var applier = BuildApplier(db2, mappings);
        await applier.ApplyLapseAsync(optionId, Now + BillingSubscription.PastDueRetryWindow, CancellationToken.None);

        await using var verify = fixture.CreateDbContext();
        var option = await verify.BillingSubscriptions.SingleAsync(s => s.Id == optionId);
        Assert.Equal(BillingSubscriptionStatus.Lapsed, option.Status);

        var grant = await verify.ModuleQuantityGrants.SingleAsync(g => g.SiteId == siteId && g.ModuleKey == new ModuleKey("channel"));
        Assert.Equal(0, grant.Quantity);

        // `adr/0160`: an option's own lapse must never touch the
        // base's own Tier/SeatLimit - the two subscriptions have no relationship a charge, or a lapse,
        // can traverse.
        var site = await verify.Sites.SingleAsync(s => s.Id == siteId);
        Assert.Equal(SubscriptionTierBands.Starter, site.Tier);
        Assert.Equal(5, site.SeatLimit);
    }

    // `23-86`: the unconditional-grant flag's own OR-read, proven against a real Postgres transaction
    // rather than argued - the exact clobbering bug this item's own brief warns about: a billing lapse
    // must not silently turn off an entitlement the platform owner unconditionally granted, and the
    // fix has to live where SubscriptionRenewalApplier's own writes land, or a redelivered/late lapse
    // would still publish "revoked" and contradict what the flag promises.

    /// <summary>The fails-before for this item's own OR-logic correctness requirement: with
    /// <see cref="ModuleQuantityGrantStore.GrantAsync"/>'s own read of
    /// <see cref="ModuleQuantityGrant.EffectiveQuantity"/> reverted to the plain <c>quantity</c>
    /// parameter it published before this item (the change this test exists to catch a regression of),
    /// this test fails: the lapse below would publish - and this row would then read - <c>0</c>, not
    /// <c>1</c>, because <c>SubscriptionRenewalApplier.RevokeEntitlementAsync</c> calls
    /// <c>GrantAsync</c> exactly as it always has, unaware the flag exists. Verified by hand for this
    /// report: reverting <c>ModuleQuantityGrantStore.GrantAsync</c>'s outbox line to
    /// <c>ModuleQuantityGrantedMapper.ToEnvelope(siteId.Value, moduleKey.Value, quantity, now, idGenerator)</c>
    /// (the pre-`23-86` shape) makes <see cref="GetQuantityAsync_ForAnOptionWithTheFlagSet_SurvivesALapse_ButNotOnceTheFlagIsLifted"/>
    /// fail its first assertion (expected 1, actual 0); restoring the OR-aware read makes it pass
    /// again.</summary>
    [Fact]
    public async Task GetQuantityAsync_ForAnOptionWithTheFlagSet_SurvivesALapse_ButNotOnceTheFlagIsLifted()
    {
        var (siteId, _) = await SeedSucceededSubscriptionAsync(seats: 5, tier: SubscriptionTierBands.Starter, periodEnd: Now + TimeSpan.FromDays(20));
        var optionId = await SeedDueOptionSubscriptionAsync(siteId, new BillingOptionKey("channel-telegram"), periodEnd: Now);
        var mappings = new Dictionary<string, string?> { ["channel-telegram"] = "channel" };
        var moduleKey = new ModuleKey("channel");

        await using (var db = fixture.CreateDbContext())
        {
            // A real payment renews the option first - the grant a lapse is about to threaten has to
            // be real, not merely the flag alone.
            await BuildApplier(db, mappings).ApplyRenewalSuccessAsync(optionId, Now, 0, 0, [], CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            // `23-86` case 1: the platform owner unconditionally grants the identical entitlement by
            // hand - a support decision independent of the payment that already renewed it.
            var grants = new ModuleQuantityGrantStore(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator(), new SystemClock());
            await grants.SetUnconditionalGrantAsync(
                siteId, moduleKey, true, "owner-sub-abc", "keeping this on during a support investigation",
                Now, CancellationToken.None);
        }

        await MarkPastDueAsync(optionId, Now);

        await using (var db = fixture.CreateDbContext())
        {
            // The billing lapse - SubscriptionRenewalApplier's own RevokeEntitlementAsync, completely
            // unaware the flag exists, calling GrantAsync exactly as it did before this item.
            await BuildApplier(db, mappings).ApplyLapseAsync(optionId, Now + BillingSubscription.PastDueRetryWindow, CancellationToken.None);
        }

        await using (var verify = fixture.CreateDbContext())
        {
            var grants = new ModuleQuantityGrantStore(verify, new EfOutboxWriter<AgoChatDbContext>(verify), new UuidV7Generator(), new SystemClock());
            // The billing lapse happened - Quantity itself really did go to zero, proving this is not
            // a test where the lapse silently no-ops.
            var row = await verify.ModuleQuantityGrants.AsNoTracking().SingleAsync(g => g.SiteId == siteId && g.ModuleKey == moduleKey);
            Assert.Equal(0, row.Quantity);

            // But the flag protects the effective read - and the published event, which is what a
            // downstream consumer (ago-calendar's own ModuleQuantityGrantedConsumer, for a different
            // module key) trusts as the final word rather than re-deriving it.
            Assert.Equal(1, await grants.GetQuantityAsync(siteId, moduleKey, CancellationToken.None));

            var latestOutboxRow = await verify.Set<OutboxMessage>()
                .Where(o => o.Type == nameof(ModuleQuantityGranted) && o.PartitionKey == siteId.Value.ToString())
                .OrderByDescending(o => o.OccurredAt)
                .FirstAsync();
            var contract = System.Text.Json.JsonSerializer.Deserialize<ModuleQuantityGranted>(latestOutboxRow.Payload)!;
            Assert.Equal(1, contract.Quantity);
        }

        // Lifting the flag re-evaluates billing at that moment (this item's own text) - billing has
        // already lapsed, so the entitlement now genuinely goes off.
        await using (var db = fixture.CreateDbContext())
        {
            var grants = new ModuleQuantityGrantStore(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator(), new SystemClock());
            await grants.SetUnconditionalGrantAsync(
                siteId, moduleKey, false, "owner-sub-abc", "support investigation concluded, billing lapsed",
                Now + BillingSubscription.PastDueRetryWindow, CancellationToken.None);
        }

        await using var finalVerify = fixture.CreateDbContext();
        var finalGrants = new ModuleQuantityGrantStore(finalVerify, new EfOutboxWriter<AgoChatDbContext>(finalVerify), new UuidV7Generator(), new SystemClock());
        Assert.Equal(0, await finalGrants.GetQuantityAsync(siteId, moduleKey, CancellationToken.None));
    }

    [Fact]
    public async Task ApplyLapseAsync_ForTheBaseSubscription_NeverTouchesAnOptionsOwnEntitlement()
    {
        // The reverse direction of `adr/0160`'s decision - a paid option is not cancelled by a base
        // lapse, it runs on its own subscription and its own money: the base lapsing must not revoke an
        // option that is still paid for.
        var (siteId, baseId) = await SeedSucceededSubscriptionAsync(seats: 5, tier: SubscriptionTierBands.Starter, periodEnd: Now);
        var optionId = await SeedDueOptionSubscriptionAsync(siteId, new BillingOptionKey("channel-telegram"), periodEnd: Now + TimeSpan.FromDays(20));
        var mappings = new Dictionary<string, string?> { ["channel-telegram"] = "channel" };

        await using (var db = fixture.CreateDbContext())
        {
            await BuildApplier(db, mappings).ApplyRenewalSuccessAsync(optionId, Now, 0, 0, [], CancellationToken.None);
        }

        await using var db2 = fixture.CreateDbContext();
        await BuildApplier(db2, mappings).ApplyLapseAsync(baseId, Now, CancellationToken.None);

        await using var verify = fixture.CreateDbContext();
        var site = await verify.Sites.SingleAsync(s => s.Id == siteId);
        Assert.Equal("free", site.Tier);
        Assert.Equal(1, site.SeatLimit);

        var grant = await verify.ModuleQuantityGrants.SingleAsync(g => g.SiteId == siteId && g.ModuleKey == new ModuleKey("channel"));
        Assert.Equal(1, grant.Quantity);
    }

    [Fact]
    public async Task ApplyRenewalSuccessAsync_ForAnOptionWithNoConfiguredEntitlementMapping_ThrowsRatherThanGrantingSilently()
    {
        var (siteId, _) = await SeedSucceededSubscriptionAsync(seats: 5, tier: SubscriptionTierBands.Starter, periodEnd: Now + TimeSpan.FromDays(20));
        var optionId = await SeedDueOptionSubscriptionAsync(siteId, new BillingOptionKey("channel-telegram"), periodEnd: Now);

        await using var db = fixture.CreateDbContext();
        // Deliberately empty - this deployment has not declared BillingOptionEntitlements:channel-telegram.
        var applier = BuildApplier(db, new Dictionary<string, string?>());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => applier.ApplyRenewalSuccessAsync(optionId, Now, 0, 0, [], CancellationToken.None));
    }

    [Fact]
    public async Task SaveAsync_ThenGetByIdAsync_PersistsAnOptionSubscription_AlignedToTheBasesPeriod()
    {
        var (siteId, _) = await SeedSucceededSubscriptionAsync(seats: 5, tier: SubscriptionTierBands.Starter, periodEnd: Now + TimeSpan.FromDays(237));

        await using var db = fixture.CreateDbContext();
        var subscriptions = new BillingSubscriptionRepository(db);
        var basePeriodEnd = (await subscriptions.GetBaseForSiteAsync(siteId, CancellationToken.None))!.CurrentPeriodEnd!.Value;

        var optionId = new BillingSubscriptionId(Guid.NewGuid());
        var option = BillingSubscription.CreateOption(optionId, siteId, "pmt_option_write_down", new BillingOptionKey("channel-telegram"), Now);
        option.MarkSucceeded("card_on_file", Now, alignedPeriodEnd: basePeriodEnd);
        await subscriptions.SaveAsync(option, CancellationToken.None);

        await using var verify = fixture.CreateDbContext();
        var reloaded = await verify.BillingSubscriptions.SingleAsync(s => s.Id == optionId);
        Assert.Equal(new BillingOptionKey("channel-telegram"), reloaded.OptionKey);
        Assert.Equal(basePeriodEnd, reloaded.CurrentPeriodEnd);
        Assert.Equal(Now + TimeSpan.FromDays(237), reloaded.CurrentPeriodEnd);
    }

    [Fact]
    public async Task GetBaseForSiteAsync_UnlikeGetLatestForSiteAsync_IgnoresANewerOptionRow()
    {
        var (siteId, baseId) = await SeedSucceededSubscriptionAsync(seats: 5, tier: SubscriptionTierBands.Starter, periodEnd: Now + TimeSpan.FromDays(20));
        var optionId = await SeedDueOptionSubscriptionAsync(siteId, new BillingOptionKey("channel-telegram"), periodEnd: Now + TimeSpan.FromDays(20));

        await using var db = fixture.CreateDbContext();
        var subscriptions = new BillingSubscriptionRepository(db);

        var latest = await subscriptions.GetLatestForSiteAsync(siteId, CancellationToken.None);
        Assert.Equal(optionId, latest!.Id);

        var baseOnly = await subscriptions.GetBaseForSiteAsync(siteId, CancellationToken.None);
        Assert.Equal(baseId, baseOnly!.Id);
    }

    /// <summary>`25-84`: the auto-bill path's own settlement actually reaching Postgres - the half
    /// `ProcessSubscriptionRenewalHandlerTests` cannot prove, because it stops at handing the applier a
    /// list. `docs/backlog/25-84-*.md` asks that the accrued charge appear "as a line item on their next
    /// regular invoice"; this codebase has no invoice aggregate, so the durable half of that promise is
    /// a `download_overage_charges` row written in the same transaction as the renewal itself, carrying
    /// the month, the bytes and the price version the amount was computed from. Read back through a
    /// second, independent context - never the one that wrote it.</summary>
    [Fact]
    public async Task ApplyRenewalSuccessAsync_WritesTheDownloadOverageLedgerRows_InTheSameTransaction()
    {
        var (siteId, subscriptionId) = await SeedSucceededSubscriptionAsync(
            seats: 5, tier: SubscriptionTierBands.Starter, periodEnd: Now);
        var periodMonth = new DateOnly(Now.Year, Now.Month, 1);

        await using (var db = fixture.CreateDbContext())
        {
            await BuildApplier(db, new Dictionary<string, string?>()).ApplyRenewalSuccessAsync(
                subscriptionId, Now, 1, 1,
                [new DownloadOverageInvoiceLine(periodMonth, OutstandingBytes: 2147483648, AmountRub: 200m, PriceVersion: 1)],
                CancellationToken.None);
        }

        await using var verify = fixture.CreateDbContext();
        var charge = await verify.DownloadOverageCharges.SingleAsync(c => c.SiteId == siteId);

        Assert.Equal(DownloadOverageChargeSource.Invoice, charge.Source);
        // Born Succeeded - the money already moved by the time this row is written, which is the whole
        // "charge first, commit the verified outcome second" ordering `13-03` established.
        Assert.Equal(DownloadOverageChargeStatus.Succeeded, charge.Status);
        Assert.Equal(periodMonth, charge.PeriodMonth);
        Assert.Equal(2147483648, charge.BytesOver);
        Assert.Equal(200m, charge.AmountRub);
        Assert.Equal(1, charge.PriceVersion);
        // No payment of its own - the money moved as part of the renewal charge.
        Assert.Null(charge.YooKassaPaymentId);
        Assert.Equal(Now, charge.SettledAt);

        // And the renewal it rode along with really did commit, in the same transaction.
        var subscription = await verify.BillingSubscriptions.SingleAsync(sub => sub.Id == subscriptionId);
        Assert.Equal(BillingSubscriptionStatus.Succeeded, subscription.Status);
    }

    /// <summary>The overwhelmingly common case: an ordinary renewal with nothing outstanding writes no
    /// ledger row at all. The control that proves the test above is writing a row because it was handed
    /// a line, not because every renewal writes one.</summary>
    [Fact]
    public async Task ApplyRenewalSuccessAsync_WithNoOverageLines_WritesNoLedgerRowAtAll()
    {
        var (siteId, subscriptionId) = await SeedSucceededSubscriptionAsync(
            seats: 5, tier: SubscriptionTierBands.Starter, periodEnd: Now);

        await using (var db = fixture.CreateDbContext())
        {
            await BuildApplier(db, new Dictionary<string, string?>()).ApplyRenewalSuccessAsync(
                subscriptionId, Now, 1, 1, [], CancellationToken.None);
        }

        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.DownloadOverageCharges.AnyAsync(c => c.SiteId == siteId));
    }

    private static SubscriptionRenewalApplier BuildApplier(AgoChatDbContext db, IReadOnlyDictionary<string, string?> entitlementMappings)
    {
        var outbox = new EfOutboxWriter<AgoChatDbContext>(db);
        var idGenerator = new UuidV7Generator();
        var entitlementGrants = new ModuleQuantityGrantStore(db, outbox, idGenerator, new SystemClock());
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            entitlementMappings.ToDictionary(kv => $"{ConfiguredBillingOptionEntitlementProvider.SectionName}:{kv.Key}", kv => kv.Value)).Build();
        var optionEntitlements = new ConfiguredBillingOptionEntitlementProvider(config);
        // `25-170`: the real OperatorRoleSeatReconciler, not a fake - this class exists precisely to
        // prove the automatic-disable behaviour against a real Postgres, the same "never mock the
        // database for a guarantee the schema itself provides" discipline testing.md states.
        var roleSeatReconciler = new OperatorRoleSeatReconciler(new OperatorRoleRepository(db));
        return new SubscriptionRenewalApplier(db, outbox, idGenerator, entitlementGrants, optionEntitlements, roleSeatReconciler);
    }

    /// <summary>Seeds an option subscription, already `Succeeded` (mirroring `SeedSucceededSubscriptionAsync`'s
    /// own shape for the base) with <paramref name="periodEnd"/> set directly by SQL for the identical
    /// reason that helper's own remarks give - the domain has no "set an arbitrary period end"
    /// writer.</summary>
    private async Task<BillingSubscriptionId> SeedDueOptionSubscriptionAsync(SiteId siteId, BillingOptionKey optionKey, DateTimeOffset periodEnd)
    {
        var optionId = new BillingSubscriptionId(Guid.NewGuid());

        await using (var db = fixture.CreateDbContext())
        {
            var option = BillingSubscription.CreateOption(optionId, siteId, $"pmt_{optionId.Value:N}", optionKey, Now - BillingSubscription.PeriodLength);
            // The value passed here is immediately overwritten by the direct SQL update below (the
            // domain has no "set an arbitrary period end" writer - SeedSucceededSubscriptionAsync's own
            // remarks); any value satisfying MarkSucceeded's own "an option must be given one" guard
            // will do.
            option.MarkSucceeded("card_on_file", Now - BillingSubscription.PeriodLength, alignedPeriodEnd: Now);
            db.BillingSubscriptions.Add(option);
            await db.SaveChangesAsync();
        }

        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
            "UPDATE billing_subscriptions SET current_period_end = @periodEnd WHERE id = @id", connection))
        {
            command.Parameters.AddWithValue("periodEnd", periodEnd);
            command.Parameters.AddWithValue("id", optionId.Value);
            await command.ExecuteNonQueryAsync();
        }

        return optionId;
    }

    private SubscriptionRenewalJob CreateJob(string yooKassaBaseUrl, IClock clock) => new(
        new DirectScopeFactory(fixture, clock, yooKassaBaseUrl),
        clock,
        Options.Create(new SubscriptionRenewalJobOptions()),
        NullLogger<SubscriptionRenewalJob>.Instance);

    /// <summary>`25-43`: publishes a fresh version of both seat-pricing keys, at
    /// <see cref="BaseSeatPriceRub"/>/<see cref="PricePerExtraSeatRub"/>, against the real Postgres
    /// container this test class shares - not the migration's own seed data (which this class never
    /// relies on, deliberately, so this file's own numbers stay whatever this file says they are,
    /// independent of what a future change to the seed migration's own amounts might be). Called once
    /// per seeded subscription, so <see cref="ProcessSubscriptionRenewalHandler"/>'s own
    /// <c>FindCurrentAsync</c> reads exactly this value at charge time - every test in this file seeds
    /// a subscription before triggering a renewal, and never republishes a *different* amount
    /// mid-test, so "the freshest published version" and "what this subscription was actually last
    /// charged" agree throughout.</summary>
    private static async Task<(int BaseSeatPriceVersion, int ExtraSeatPriceVersion)> SeedCurrentSeatPricesAsync(
        AgoChatDbContext db, DateTimeOffset publishedAt)
    {
        var prices = new PriceCatalogRepository(db);

        var baseResource = await prices.GetByKeyAsync(SubscriptionTierBands.BaseSeatPriceKey, CancellationToken.None)
            ?? PricedResource.Create(new PricedResourceId(Guid.NewGuid()), SubscriptionTierBands.BaseSeatPriceKey);
        var baseVersion = baseResource.Publish(new PublishedPriceVersionId(Guid.NewGuid()), BaseSeatPriceRub, publishedAt);
        await prices.SaveAsync(baseResource, CancellationToken.None);

        var extraResource = await prices.GetByKeyAsync(SubscriptionTierBands.ExtraSeatPriceKey, CancellationToken.None)
            ?? PricedResource.Create(new PricedResourceId(Guid.NewGuid()), SubscriptionTierBands.ExtraSeatPriceKey);
        var extraVersion = extraResource.Publish(new PublishedPriceVersionId(Guid.NewGuid()), PricePerExtraSeatRub, publishedAt);
        await prices.SaveAsync(extraResource, CancellationToken.None);

        return (baseVersion.Sequence, extraVersion.Sequence);
    }

    private async Task<(SiteId SiteId, BillingSubscriptionId SubscriptionId)> SeedSucceededSubscriptionAsync(
        int seats, string tier, DateTimeOffset periodEnd)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var subscriptionId = new BillingSubscriptionId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", [], tier: tier, seatLimit: seats));

        var (baseSeatPriceVersion, extraSeatPriceVersion) = await SeedCurrentSeatPricesAsync(db, Now - BillingSubscription.PeriodLength);
        var subscription = BillingSubscription.Create(
            subscriptionId, siteId, $"pmt_{subscriptionId.Value:N}", seats, tier, baseSeatPriceVersion, extraSeatPriceVersion,
            Now - BillingSubscription.PeriodLength);
        subscription.MarkSucceeded("card_on_file", Now - BillingSubscription.PeriodLength);
        db.BillingSubscriptions.Add(subscription);
        await db.SaveChangesAsync();

        // CurrentPeriodEnd was set by MarkSucceeded to (Now - PeriodLength) + PeriodLength = Now; every
        // test above passes its own periodEnd explicitly, so overwrite it directly via SQL - the
        // domain has no "set an arbitrary period end" writer, deliberately (BillingSubscription's own
        // remarks: only MarkSucceeded/RecordRenewalSuccess ever move it).
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
            "UPDATE billing_subscriptions SET current_period_end = @periodEnd WHERE id = @id", connection))
        {
            command.Parameters.AddWithValue("periodEnd", periodEnd);
            command.Parameters.AddWithValue("id", subscriptionId.Value);
            await command.ExecuteNonQueryAsync();
        }

        return (siteId, subscriptionId);
    }

    private async Task MarkPastDueAsync(BillingSubscriptionId subscriptionId, DateTimeOffset pastDueSince)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE billing_subscriptions SET status = 'PastDue', past_due_since = @pastDueSince, last_renewal_attempt_at = @pastDueSince WHERE id = @id",
            connection);
        command.Parameters.AddWithValue("pastDueSince", pastDueSince);
        command.Parameters.AddWithValue("id", subscriptionId.Value);
        await command.ExecuteNonQueryAsync();
    }

    private async Task RequestCancellationAsync(BillingSubscriptionId subscriptionId, DateTimeOffset now)
    {
        await using var db = fixture.CreateDbContext();
        var subscription = await db.BillingSubscriptions.SingleAsync(s => s.Id == subscriptionId);
        subscription.RequestCancellation(now);
        await db.SaveChangesAsync();
    }

    private async Task ScheduleSeatDecreaseAsync(BillingSubscriptionId subscriptionId, int newSeatCount, string newTier)
    {
        await using var db = fixture.CreateDbContext();
        var subscription = await db.BillingSubscriptions.SingleAsync(s => s.Id == subscriptionId);
        subscription.ScheduleSeatDecrease(newSeatCount, newTier);
        await db.SaveChangesAsync();
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed record TestHost(WebApplication App, string BaseUrl) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    /// <summary>The identical fake ЮKassa Kestrel host <c>YooKassaPaymentsApiClientTests</c> already
    /// established - reused verbatim rather than a second fake HTTP client for the same third
    /// party.</summary>
    private static async Task<TestHost> BuildFakeYooKassaHostAsync(Action<WebApplication> configureRoutes)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        configureRoutes(app);

        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        var baseUrl = addresses.First() + "/";

        return new TestHost(app, baseUrl);
    }

    /// <summary>
    /// The job's own production shape resolves <see cref="IBillingSubscriptionRepository"/> (to list
    /// due candidates) and <see cref="ProcessSubscriptionRenewalHandler"/> (per candidate) from a
    /// fresh <see cref="IServiceScopeFactory"/> scope - <see cref="AutoCloseInactiveConversationsJobTests.DirectScopeFactory"/>'s
    /// own precedent, extended to resolve two types instead of one since this job's own
    /// <see cref="SubscriptionRenewalJob.RunOnceAsync"/> needs both out of the same kind of scope.
    /// </summary>
    private sealed class DirectScopeFactory(
        PostgresFixture fixture, IClock clock, string yooKassaBaseUrl,
        IReadOnlyDictionary<string, string?>? entitlementMappings = null)
        : IServiceScopeFactory
    {
        public IServiceScope CreateScope()
        {
            var db = fixture.CreateDbContext();
            var subscriptions = new BillingSubscriptionRepository(db);
            var outbox = new EfOutboxWriter<AgoChatDbContext>(db);
            var idGenerator = new UuidV7Generator();
            var entitlementGrants = new ModuleQuantityGrantStore(db, outbox, idGenerator, new SystemClock());
            // `23-86`: the real ConfiguredBillingOptionEntitlementProvider, backed by an in-memory
            // configuration rather than a fake - proves the actual `BillingOptionEntitlements:<key>`
            // lookup this deployment will configure, not a stand-in for it. Empty for every test that
            // never touches an option row.
            var entitlementConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(entitlementMappings ?? new Dictionary<string, string?>())
                .Build();
            var optionEntitlements = new ConfiguredBillingOptionEntitlementProvider(entitlementConfig);
            // `25-170`: the real OperatorRoleSeatReconciler - see BuildApplier's own remarks above for
            // why this is never faked.
            var roleSeatReconciler = new OperatorRoleSeatReconciler(new OperatorRoleRepository(db));
            var applier = new SubscriptionRenewalApplier(db, outbox, idGenerator, entitlementGrants, optionEntitlements, roleSeatReconciler);

            var httpClient = new HttpClient { BaseAddress = new Uri(yooKassaBaseUrl) };
            var yooKassa = new YooKassaPaymentsApiClient(httpClient);

            var prices = new PriceCatalogRepository(db);
            // `25-84`: real stores throughout - the renewal now reads the site's own billing mode and
            // its tier's own thresholds before deciding whether to sweep any download overage onto the
            // charge, so a fake here would prove nothing about what the job actually does.
            var handler = new ProcessSubscriptionRenewalHandler(
                subscriptions, new SiteRepository(db), yooKassa, prices,
                new DownloadThresholdReadStore(fixture.DataSource),
                new DownloadOverageReadStore(fixture.DataSource),
                applier, clock);

            var services = new Dictionary<Type, object>
            {
                [typeof(IBillingSubscriptionRepository)] = subscriptions,
                [typeof(ProcessSubscriptionRenewalHandler)] = handler,
            };

            return new DirectScope(db, httpClient, services);
        }

        private sealed class DirectScope(AgoChatDbContext db, HttpClient httpClient, Dictionary<Type, object> services) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new MultiServiceProvider(services);

            public void Dispose()
            {
                db.Dispose();
                httpClient.Dispose();
            }
        }

        private sealed class MultiServiceProvider(Dictionary<Type, object> services) : IServiceProvider
        {
            public object? GetService(Type serviceType) => services.GetValueOrDefault(serviceType);
        }
    }
}
