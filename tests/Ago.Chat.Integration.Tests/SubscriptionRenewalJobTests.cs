using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.CreateCheckoutSession;
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

    private static readonly BillingOptions Billing = new() { BaseSeatPriceRub = 500m, PricePerExtraSeatRub = 100m, CheckoutReturnUrl = "https://console.example/return" };

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

        await applier.ApplyRenewalSuccessAsync(optionId, Now, CancellationToken.None);

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
            await BuildApplier(db, mappings).ApplyRenewalSuccessAsync(optionId, Now, CancellationToken.None);
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
            await BuildApplier(db, mappings).ApplyRenewalSuccessAsync(optionId, Now, CancellationToken.None);
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
            () => applier.ApplyRenewalSuccessAsync(optionId, Now, CancellationToken.None));
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

    private static SubscriptionRenewalApplier BuildApplier(AgoChatDbContext db, IReadOnlyDictionary<string, string?> entitlementMappings)
    {
        var outbox = new EfOutboxWriter<AgoChatDbContext>(db);
        var idGenerator = new UuidV7Generator();
        var entitlementGrants = new ModuleQuantityGrantStore(db, outbox, idGenerator);
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            entitlementMappings.ToDictionary(kv => $"{ConfiguredBillingOptionEntitlementProvider.SectionName}:{kv.Key}", kv => kv.Value)).Build();
        var optionEntitlements = new ConfiguredBillingOptionEntitlementProvider(config);
        return new SubscriptionRenewalApplier(db, outbox, idGenerator, entitlementGrants, optionEntitlements);
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

    private async Task<(SiteId SiteId, BillingSubscriptionId SubscriptionId)> SeedSucceededSubscriptionAsync(
        int seats, string tier, DateTimeOffset periodEnd)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var subscriptionId = new BillingSubscriptionId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", [], tier: tier, seatLimit: seats));

        var subscription = BillingSubscription.Create(
            subscriptionId, siteId, $"pmt_{subscriptionId.Value:N}", seats, tier, Now - BillingSubscription.PeriodLength);
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
            var entitlementGrants = new ModuleQuantityGrantStore(db, outbox, idGenerator);
            // `23-86`: the real ConfiguredBillingOptionEntitlementProvider, backed by an in-memory
            // configuration rather than a fake - proves the actual `BillingOptionEntitlements:<key>`
            // lookup this deployment will configure, not a stand-in for it. Empty for every test that
            // never touches an option row.
            var entitlementConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(entitlementMappings ?? new Dictionary<string, string?>())
                .Build();
            var optionEntitlements = new ConfiguredBillingOptionEntitlementProvider(entitlementConfig);
            var applier = new SubscriptionRenewalApplier(db, outbox, idGenerator, entitlementGrants, optionEntitlements);

            var httpClient = new HttpClient { BaseAddress = new Uri(yooKassaBaseUrl) };
            var yooKassa = new YooKassaPaymentsApiClient(httpClient);

            var handler = new ProcessSubscriptionRenewalHandler(subscriptions, yooKassa, Billing, applier, clock);

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
