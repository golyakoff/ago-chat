using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.CreateCheckoutSession;
using Ago.Chat.Application.UseCases.ProcessSubscriptionRenewal;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.ProcessSubscriptionRenewal;

/// <summary>`23-86`: this handler's own recurring-charge amount computation is base-seat-priced only
/// (`SubscriptionTierBands.ComputeSeatPriceRub` against `RequestedSeats`) - meaningless, and silently
/// wrong, for an option subscription, since an option is priced flat. The handler guards this with an
/// explicit `IsOption` check that diverts to <see cref="ChannelAddOnPricing.PriceKeyFor"/> before the
/// seat formula is ever reached, so the specific wrong number that formula would produce for
/// `RequestedSeats == 0` never actually matters. `26-278` taught this branch to actually charge a
/// `channel-*` option instead of always throwing (see the tests further down this file) - the one case
/// still thrown on, proven here, is an option key this codebase does not know how to price at all (an
/// AI option), the identical "no price, anywhere" refusal `23-86` originally built for every option.</summary>
public class ProcessSubscriptionRenewalHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HandleAsync_WhenTheDueSubscriptionIsAnOptionWithNoKnownPriceKey_ThrowsRatherThanChargingAWrongOrZeroAmount()
    {
        var optionId = new BillingSubscriptionId(Guid.NewGuid());
        // `ai-processing` - ChannelAddOnPricing.PriceKeyFor returns null for it (0012's own "для ИИ -
        // нет, и поэтому цены не публикуются"), unlike a `channel-*` key which `26-278` now prices.
        var option = BillingSubscription.CreateOption(optionId, SiteId, "pmt_option", new BillingOptionKey("ai-processing"), Now - BillingSubscription.PeriodLength);
        option.MarkSucceeded("card_on_file", Now - BillingSubscription.PeriodLength, alignedPeriodEnd: Now);

        var subscriptions = new FakeBillingSubscriptionRepository();
        subscriptions.Seed(option);
        var yooKassa = new FakeYooKassaPaymentsClient();
        // `25-43`: never seeded - the IsOption guard this test proves fires before either seat-pricing
        // key is ever read, so an empty catalog is the honest fixture, not an oversight.
        var prices = new FakePriceCatalogRepository();
        var applier = new FakeSubscriptionRenewalApplier();
        // `25-84`: an empty site repository, empty thresholds and an empty overage store - the
        // IsOption guard this test proves fires before any of the three is read, exactly as the
        // empty price catalog above already documents for itself.
        var sites = new FakeSiteRepository();
        var thresholds = new FakeDownloadThresholdReadStore();
        var overageReads = new FakeDownloadOverageReadStore();
        var handler = new ProcessSubscriptionRenewalHandler(subscriptions, sites, yooKassa, prices, thresholds, overageReads, applier, new FakeClock(Now));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(new Application.UseCases.ProcessSubscriptionRenewal.ProcessSubscriptionRenewal(optionId), CancellationToken.None));

        // No charge was ever attempted, and the applier was never told any outcome - the refusal
        // happens before either, so a misconfigured deployment cannot silently charge Rub 0 or leave a
        // half-applied outcome behind.
        Assert.Null(yooKassa.LastChargeRequest);
        Assert.Empty(applier.RenewedSuccessfully);
        Assert.Empty(applier.RenewalFailures);
        Assert.Empty(applier.Lapsed);
    }

    // ----------------------------------------------------------------------------------------------
    // `26-278`: a channel option's own recurring charge - ChannelAddOnPricing.PriceKeyFor resolves a
    // `channel-*` option key to the flat `channel-addon` price, and this handler now charges it instead
    // of throwing (the throw above is reserved for an option key with no known price at all).
    // ----------------------------------------------------------------------------------------------

    private static (ProcessSubscriptionRenewalHandler Handler, FakeYooKassaPaymentsClient YooKassa, FakeSubscriptionRenewalApplier Applier, BillingSubscriptionId OptionId)
        CreateDueChannelOptionFixture(bool seedPrice = true)
    {
        var optionId = new BillingSubscriptionId(Guid.NewGuid());
        var option = BillingSubscription.CreateOption(
            optionId, SiteId, "pmt_option", new BillingOptionKey("channel-telegram"), Now - BillingSubscription.PeriodLength);
        option.MarkSucceeded("card_on_file", Now - BillingSubscription.PeriodLength, alignedPeriodEnd: Now);

        var subscriptions = new FakeBillingSubscriptionRepository();
        subscriptions.Seed(option);
        var yooKassa = new FakeYooKassaPaymentsClient();
        var prices = new FakePriceCatalogRepository();
        if (seedPrice)
        {
            prices.SeedVersion(ChannelAddOnPricing.ChannelAddOnKey, 100m, Now - BillingSubscription.PeriodLength);
        }

        var applier = new FakeSubscriptionRenewalApplier();
        var sites = new FakeSiteRepository();
        var thresholds = new FakeDownloadThresholdReadStore();
        var overageReads = new FakeDownloadOverageReadStore();
        var handler = new ProcessSubscriptionRenewalHandler(subscriptions, sites, yooKassa, prices, thresholds, overageReads, applier, new FakeClock(Now));

        return (handler, yooKassa, applier, optionId);
    }

    [Fact]
    public async Task HandleAsync_WhenADueChannelOptionsPriceIsPublished_ChargesTheFlatChannelAddOnPrice_AndAppliesRenewalSuccess()
    {
        var (handler, yooKassa, applier, optionId) = CreateDueChannelOptionFixture();

        var outcome = await handler.HandleAsync(
            new Application.UseCases.ProcessSubscriptionRenewal.ProcessSubscriptionRenewal(optionId), CancellationToken.None);

        Assert.IsType<SubscriptionRenewalOutcome.Renewed>(outcome);
        Assert.NotNull(yooKassa.LastChargeRequest);
        Assert.Equal(100m, yooKassa.LastChargeRequest!.AmountRub);
        Assert.Single(applier.RenewedSuccessfully);
        Assert.Equal(optionId, applier.RenewedSuccessfully[0]);
        // `25-43`: meaningless for an option row - passed as 0/0, the identical convention
        // BillingSubscription.CreateOption's own remarks already establish for RequestedSeats/Tier.
        Assert.Equal((0, 0), applier.RenewedWithPriceVersions[0]);
    }

    [Fact]
    public async Task HandleAsync_WhenADueChannelOptionsPriceIsNotPublished_Throws()
    {
        var (handler, yooKassa, applier, optionId) = CreateDueChannelOptionFixture(seedPrice: false);

        // `26-278`'s own author decision (2026-09-29): a missing channel-addon price at renewal throws,
        // matching the seat prices - a channel option reached Succeeded at least once already, so it
        // was priced at purchase; a deployment that has since un-published the key is a regression, not
        // an ordinary "not for sale" state.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(new Application.UseCases.ProcessSubscriptionRenewal.ProcessSubscriptionRenewal(optionId), CancellationToken.None));

        Assert.Null(yooKassa.LastChargeRequest);
        Assert.Empty(applier.RenewedSuccessfully);
        Assert.Empty(applier.RenewalFailures);
    }

    [Fact]
    public async Task HandleAsync_WhenTheChannelOptionsChargeIsRefused_AppliesRenewalFailure()
    {
        var (handler, yooKassa, applier, optionId) = CreateDueChannelOptionFixture();
        yooKassa.ChargeResult = new ChargeStoredPaymentMethodResult.Refused("card_declined");

        var outcome = await handler.HandleAsync(
            new Application.UseCases.ProcessSubscriptionRenewal.ProcessSubscriptionRenewal(optionId), CancellationToken.None);

        Assert.IsType<SubscriptionRenewalOutcome.ChargeRefused>(outcome);
        Assert.Single(applier.RenewalFailures);
        Assert.Equal(optionId, applier.RenewalFailures[0]);
        Assert.Empty(applier.RenewedSuccessfully);
    }

    // ----------------------------------------------------------------------------------------------
    // `25-84`: the auto-bill path's own settlement - "the accumulated charge appearing as a line item
    // on their next regular invoice" (docs/backlog/25-84-*.md). This codebase has no invoice entity, so
    // a "line item" is three real things: the renewal charge grows by the overage, the payment's own
    // description names it, and a ledger row records it - see `DownloadOverageInvoiceLine`'s own
    // remarks. All three are asserted here.
    // ----------------------------------------------------------------------------------------------

    private const long OneGibibyte = 1024L * 1024L * 1024L;

    private sealed record RenewalFixture(
        ProcessSubscriptionRenewalHandler Handler,
        BillingSubscriptionId SubscriptionId,
        FakeYooKassaPaymentsClient YooKassa,
        FakeSubscriptionRenewalApplier Applier,
        Site Site,
        FakeDownloadOverageReadStore OverageReads,
        FakeDownloadThresholdReadStore Thresholds,
        FakePriceCatalogRepository Prices);

    /// <summary>One `Succeeded`, due-for-renewal seat subscription on a site whose tier has a
    /// one-gibibyte hard download threshold, with both seat prices and the overage price published.
    /// The baseline seat charge is 490 RUB (`Stage25AddPricedResourceCatalog`'s own seeded figures at
    /// `BaseSeats` or fewer), so every overage assertion below is a delta against exactly that.</summary>
    private static RenewalFixture CreateDueRenewal()
    {
        var subscriptionId = new BillingSubscriptionId(Guid.NewGuid());
        var createdAt = Now - BillingSubscription.PeriodLength;
        var subscription = BillingSubscription.Create(
            subscriptionId, SiteId, "pmt_base", requestedSeats: 3, tier: SubscriptionTierBands.Starter,
            baseSeatPriceVersion: 1, extraSeatPriceVersion: 1, createdAt);
        subscription.MarkSucceeded("card_on_file", createdAt);

        var subscriptions = new FakeBillingSubscriptionRepository();
        subscriptions.Seed(subscription);

        var site = new Site(SiteId, "pk-" + SiteId.Value, allowedOrigins: [], tier: SubscriptionTierBands.Starter);
        var sites = new FakeSiteRepository();
        sites.Seed(site);

        var prices = new FakePriceCatalogRepository();
        prices.SeedVersion(SubscriptionTierBands.BaseSeatPriceKey, 490m, createdAt);
        prices.SeedVersion(SubscriptionTierBands.ExtraSeatPriceKey, 200m, createdAt);
        prices.SeedVersion(DownloadOveragePricing.OveragePerGigabyteKey, 100m, createdAt);

        var thresholds = new FakeDownloadThresholdReadStore();
        thresholds.Seed(SubscriptionTierBands.Starter, softThresholdBytes: OneGibibyte / 2, hardThresholdBytes: OneGibibyte);

        var overageReads = new FakeDownloadOverageReadStore();
        var yooKassa = new FakeYooKassaPaymentsClient();
        var applier = new FakeSubscriptionRenewalApplier();

        var handler = new ProcessSubscriptionRenewalHandler(
            subscriptions, sites, yooKassa, prices, thresholds, overageReads, applier, new FakeClock(Now));

        return new RenewalFixture(handler, subscriptionId, yooKassa, applier, site, overageReads, thresholds, prices);
    }

    private static Task<SubscriptionRenewalOutcome> RenewAsync(RenewalFixture fixture) =>
        fixture.Handler.HandleAsync(
            new Application.UseCases.ProcessSubscriptionRenewal.ProcessSubscriptionRenewal(fixture.SubscriptionId),
            CancellationToken.None);

    /// <summary>The control: nothing downloaded past the threshold, so the renewal charges exactly the
    /// seat price and nothing is swept. Every assertion below is a delta against this 490.</summary>
    [Fact]
    public async Task HandleAsync_WithNoOverage_ChargesTheSeatPriceAlone()
    {
        var fixture = CreateDueRenewal();

        var outcome = await RenewAsync(fixture);

        Assert.IsType<SubscriptionRenewalOutcome.Renewed>(outcome);
        Assert.Equal(490m, fixture.YooKassa.LastChargeRequest!.AmountRub);
        Assert.Empty(Assert.Single(fixture.Applier.RenewedWithOverageLines));
    }

    // `26-299`: reachable now that `savePaymentMethod` is the operator's own checkout-time choice - a
    // due-for-renewal base with no card on file can never auto-renew, by construction. Lapses exactly
    // like `decisions/0006`'s own cancellation path (no charge attempt), rather than the "unreachable,
    // thrown" case this guard used to be - this is the exact case the console's own billing-v2 screen
    // states plainly ahead of time.
    [Fact]
    public async Task HandleAsync_WhenTheDueSubscriptionHasNoStoredPaymentMethod_LapsesRatherThanThrowing()
    {
        var subscriptionId = new BillingSubscriptionId(Guid.NewGuid());
        var createdAt = Now - BillingSubscription.PeriodLength;
        var subscription = BillingSubscription.Create(
            subscriptionId, SiteId, "pmt_base", requestedSeats: 3, tier: SubscriptionTierBands.Starter,
            baseSeatPriceVersion: 1, extraSeatPriceVersion: 1, createdAt);
        subscription.MarkSucceeded(paymentMethodId: null, createdAt);

        var subscriptions = new FakeBillingSubscriptionRepository();
        subscriptions.Seed(subscription);
        var sites = new FakeSiteRepository();
        sites.Seed(new Site(SiteId, "pk-" + SiteId.Value, allowedOrigins: [], tier: SubscriptionTierBands.Starter));
        var prices = new FakePriceCatalogRepository();
        prices.SeedVersion(SubscriptionTierBands.BaseSeatPriceKey, 490m, createdAt);
        prices.SeedVersion(SubscriptionTierBands.ExtraSeatPriceKey, 200m, createdAt);
        var yooKassa = new FakeYooKassaPaymentsClient();
        var applier = new FakeSubscriptionRenewalApplier();
        var handler = new ProcessSubscriptionRenewalHandler(
            subscriptions, sites, yooKassa, prices, new FakeDownloadThresholdReadStore(), new FakeDownloadOverageReadStore(),
            applier, new FakeClock(Now));

        var outcome = await handler.HandleAsync(
            new Application.UseCases.ProcessSubscriptionRenewal.ProcessSubscriptionRenewal(subscriptionId), CancellationToken.None);

        Assert.IsType<SubscriptionRenewalOutcome.Lapsed>(outcome);
        Assert.Contains(subscriptionId, applier.Lapsed);
        Assert.Null(yooKassa.LastChargeRequest);
    }

    /// <summary>`docs/backlog/25-84-*.md`'s own Done-when: "auto-bill accrues onto the next invoice with
    /// no tenant action." 3 GiB over at 100 RUB/GiB is 300 RUB, added to the 490 RUB seat charge - and
    /// named in the description the tenant's own payment carries.</summary>
    [Fact]
    public async Task HandleAsync_WhenOnAutoBill_AddsTheAccruedOverageToTheRenewalCharge()
    {
        var fixture = CreateDueRenewal();
        fixture.Site.SetDownloadOverageBillingMode(DownloadOverageBillingMode.AutoBill, "owner", "agreed", Now);
        fixture.OverageReads.SeedEgress(SiteId, new DateOnly(Now.Year, Now.Month, 1), bytesOut: 4 * OneGibibyte);

        var outcome = await RenewAsync(fixture);

        Assert.IsType<SubscriptionRenewalOutcome.Renewed>(outcome);
        Assert.Equal(790m, fixture.YooKassa.LastChargeRequest!.AmountRub);
        Assert.Contains("download overage", fixture.YooKassa.LastChargeRequest.Description);
    }

    /// <summary>The same renewal also hands the applier the ledger rows to write - a per-month,
    /// per-price-version record of what the tenant was actually charged for, which is what makes the
    /// figure explainable later rather than merely collected.</summary>
    [Fact]
    public async Task HandleAsync_WhenOnAutoBill_HandsTheApplierTheLedgerLineForEachMonth()
    {
        var fixture = CreateDueRenewal();
        fixture.Site.SetDownloadOverageBillingMode(DownloadOverageBillingMode.AutoBill, "owner", "agreed", Now);
        fixture.OverageReads.SeedEgress(SiteId, new DateOnly(2026, 8, 1), bytesOut: 2 * OneGibibyte);
        fixture.OverageReads.SeedEgress(SiteId, new DateOnly(Now.Year, Now.Month, 1), bytesOut: 3 * OneGibibyte);

        await RenewAsync(fixture);

        var lines = Assert.Single(fixture.Applier.RenewedWithOverageLines);
        Assert.Equal(2, lines.Count);
        Assert.Equal(new DateOnly(2026, 8, 1), lines[0].PeriodMonth);
        Assert.Equal(100m, lines[0].AmountRub);
        Assert.Equal(new DateOnly(Now.Year, Now.Month, 1), lines[1].PeriodMonth);
        Assert.Equal(200m, lines[1].AmountRub);
        Assert.All(lines, line => Assert.Equal(1, line.PriceVersion));
        // 490 seats + 100 + 200 overage.
        Assert.Equal(790m, fixture.YooKassa.LastChargeRequest!.AmountRub);
    }

    /// <summary>A manual tenant who never completed a checkout is never swept - they sat blocked and
    /// agreed to nothing, so the residue past the threshold is not a debt
    /// (`ResolveOverageLinesAsync`'s own remarks). The identical fixture as the auto-bill case above,
    /// one field different.</summary>
    [Fact]
    public async Task HandleAsync_WhenOnManual_AndNothingWasEverBought_SweepsNothing()
    {
        var fixture = CreateDueRenewal();
        fixture.OverageReads.SeedEgress(SiteId, new DateOnly(Now.Year, Now.Month, 1), bytesOut: 4 * OneGibibyte);

        await RenewAsync(fixture);

        Assert.Equal(490m, fixture.YooKassa.LastChargeRequest!.AmountRub);
        Assert.Empty(Assert.Single(fixture.Applier.RenewedWithOverageLines));
    }

    /// <summary>A manual tenant who *did* buy their way past the block is swept for whatever accrued
    /// afterwards - "pay once, then meter", the reading of `25-84` that
    /// `GetAttachmentDownloadUrlHandler.IsOverageAuthorizedAsync`'s own remarks settle. 4 GiB over, of
    /// which 1 GiB was paid for at checkout, leaves 3 GiB at 100 RUB/GiB.</summary>
    [Fact]
    public async Task HandleAsync_WhenOnManual_ButACheckoutWasPaid_SweepsWhatAccruedAfterwards()
    {
        var fixture = CreateDueRenewal();
        var thisMonth = new DateOnly(Now.Year, Now.Month, 1);
        fixture.OverageReads.SeedEgress(SiteId, thisMonth, bytesOut: 5 * OneGibibyte);
        fixture.OverageReads.SeedCharge(SiteId, thisMonth, bytesOver: OneGibibyte, amountRub: 100m);

        await RenewAsync(fixture);

        Assert.Equal(790m, fixture.YooKassa.LastChargeRequest!.AmountRub);
        var line = Assert.Single(Assert.Single(fixture.Applier.RenewedWithOverageLines));
        Assert.Equal(3 * OneGibibyte, line.OutstandingBytes);
        Assert.Equal(300m, line.AmountRub);
    }

    /// <summary>`25-83`'s own free, indefinite owner override means free - including of anything this
    /// item would otherwise sweep onto the invoice.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheOwnerGrantedTheFreeExemption_SweepsNothing()
    {
        var fixture = CreateDueRenewal();
        fixture.Site.SetDownloadOverageBillingMode(DownloadOverageBillingMode.AutoBill, "owner", "agreed", Now);
        fixture.Site.GrantDownloadBlockExemption("owner", "long-standing customer", Now);
        fixture.OverageReads.SeedEgress(SiteId, new DateOnly(Now.Year, Now.Month, 1), bytesOut: 9 * OneGibibyte);

        await RenewAsync(fixture);

        Assert.Equal(490m, fixture.YooKassa.LastChargeRequest!.AmountRub);
        Assert.Empty(Assert.Single(fixture.Applier.RenewedWithOverageLines));
    }

    /// <summary>A deployment that never published an overage price renews normally - it does not refuse
    /// a paying customer's subscription over a feature that is simply not for sale here. The opposite
    /// posture from the two seat-pricing keys, which throw; see `ResolveOverageLinesAsync`'s own
    /// remarks.</summary>
    [Fact]
    public async Task HandleAsync_WhenNoOveragePriceWasEverPublished_StillRenewsTheSubscription()
    {
        var subscriptionId = new BillingSubscriptionId(Guid.NewGuid());
        var createdAt = Now - BillingSubscription.PeriodLength;
        var subscription = BillingSubscription.Create(
            subscriptionId, SiteId, "pmt_base", requestedSeats: 3, tier: SubscriptionTierBands.Starter, 1, 1, createdAt);
        subscription.MarkSucceeded("card_on_file", createdAt);
        var subscriptions = new FakeBillingSubscriptionRepository();
        subscriptions.Seed(subscription);

        var site = new Site(SiteId, "pk-" + SiteId.Value, allowedOrigins: [], tier: SubscriptionTierBands.Starter);
        site.SetDownloadOverageBillingMode(DownloadOverageBillingMode.AutoBill, "owner", "agreed", Now);
        var sites = new FakeSiteRepository();
        sites.Seed(site);

        var prices = new FakePriceCatalogRepository();
        prices.SeedVersion(SubscriptionTierBands.BaseSeatPriceKey, 490m, createdAt);
        prices.SeedVersion(SubscriptionTierBands.ExtraSeatPriceKey, 200m, createdAt);

        var thresholds = new FakeDownloadThresholdReadStore();
        thresholds.Seed(SubscriptionTierBands.Starter, OneGibibyte / 2, OneGibibyte);
        var overageReads = new FakeDownloadOverageReadStore();
        overageReads.SeedEgress(SiteId, new DateOnly(Now.Year, Now.Month, 1), bytesOut: 9 * OneGibibyte);

        var yooKassa = new FakeYooKassaPaymentsClient();
        var applier = new FakeSubscriptionRenewalApplier();
        var handler = new ProcessSubscriptionRenewalHandler(
            subscriptions, sites, yooKassa, prices, thresholds, overageReads, applier, new FakeClock(Now));

        var outcome = await handler.HandleAsync(
            new Application.UseCases.ProcessSubscriptionRenewal.ProcessSubscriptionRenewal(subscriptionId),
            CancellationToken.None);

        Assert.IsType<SubscriptionRenewalOutcome.Renewed>(outcome);
        Assert.Equal(490m, yooKassa.LastChargeRequest!.AmountRub);
    }

    // ----------------------------------------------------------------------------------------------
    // `26-275`: the extra-Administrator charge's own recurring correctness - purchased once
    // (`PurchaseAdministratorSlotHandler`'s own prorated first charge), it must keep billing on every
    // renewal after that, not silently lapse into free. Fixtures built by hand rather than through
    // `CreateDueRenewal` above, the same "no overage price seeded, an empty catalog is the honest
    // fixture" shape `HandleAsync_WhenTheDueSubscriptionIsAnOption_...` already uses, since these tests
    // are not about overage at all.
    // ----------------------------------------------------------------------------------------------

    /// <summary>The bug this item fixes: two extra Administrators, purchased once, must appear on
    /// every renewal charge afterwards - `seatAmount + admins × adminPrice`, exactly the same
    /// "read the currently-effective price fresh" discipline `basePrice`/`extraPrice` already prove for
    /// seats. Before this item's fix, the renewal amount was `seatAmount` alone - this test is red
    /// against that code and green only once the admin contribution is actually added.</summary>
    [Fact]
    public async Task HandleAsync_WithPurchasedExtraAdministrators_ChargesForThemOnEveryRenewal()
    {
        var subscriptionId = new BillingSubscriptionId(Guid.NewGuid());
        var createdAt = Now - BillingSubscription.PeriodLength;
        var subscription = BillingSubscription.Create(
            subscriptionId, SiteId, "pmt_base", requestedSeats: 3, tier: SubscriptionTierBands.Starter,
            baseSeatPriceVersion: 1, extraSeatPriceVersion: 1, createdAt);
        subscription.MarkSucceeded("card_on_file", createdAt);

        var subscriptions = new FakeBillingSubscriptionRepository();
        var prices = new FakePriceCatalogRepository();
        prices.SeedVersion(SubscriptionTierBands.BaseSeatPriceKey, 490m, createdAt);
        prices.SeedVersion(SubscriptionTierBands.ExtraSeatPriceKey, 200m, createdAt);
        var adminPriceVersion = prices.SeedVersion(SubscriptionTierBands.AdminExtraPriceKey, 1000m, createdAt);
        // The purchase itself, already settled before this renewal - `ApplyAdministratorPurchase` is
        // what `AdministratorSlotChangeApplier` calls in production once the prorated first charge
        // succeeds (`PurchaseAdministratorSlotHandler`'s own remarks).
        subscription.ApplyAdministratorPurchase(newExtraAdministratorCount: 2, adminPriceVersion);
        subscriptions.Seed(subscription);

        var site = new Site(SiteId, "pk-" + SiteId.Value, allowedOrigins: [], tier: SubscriptionTierBands.Starter);
        var sites = new FakeSiteRepository();
        sites.Seed(site);

        var thresholds = new FakeDownloadThresholdReadStore();
        var overageReads = new FakeDownloadOverageReadStore();
        var yooKassa = new FakeYooKassaPaymentsClient();
        var applier = new FakeSubscriptionRenewalApplier();
        var handler = new ProcessSubscriptionRenewalHandler(
            subscriptions, sites, yooKassa, prices, thresholds, overageReads, applier, new FakeClock(Now));

        var outcome = await handler.HandleAsync(
            new Application.UseCases.ProcessSubscriptionRenewal.ProcessSubscriptionRenewal(subscriptionId),
            CancellationToken.None);

        Assert.IsType<SubscriptionRenewalOutcome.Renewed>(outcome);
        // 490 seats + 2 x 1000 admin = 2490. No overage seeded, so this is the whole charge.
        Assert.Equal(2490m, yooKassa.LastChargeRequest!.AmountRub);
        Assert.Contains("extra Administrator", yooKassa.LastChargeRequest.Description);
    }

    /// <summary>A subscription that never purchased an extra Administrator (the overwhelming majority)
    /// must never be charged for one, and must never require `admin-extra` to be published at all -
    /// the identical "zero purchased means nothing to look up" shape
    /// `PurchaseAdministratorSlotHandler`'s own old-price lookup already establishes. Proven here by
    /// seeding no `admin-extra` price whatsoever and still renewing successfully.</summary>
    [Fact]
    public async Task HandleAsync_WithNoExtraAdministratorsPurchased_NeverRequiresAnAdminPriceToBePublished()
    {
        var fixture = CreateDueRenewal();

        var outcome = await RenewAsync(fixture);

        Assert.IsType<SubscriptionRenewalOutcome.Renewed>(outcome);
        Assert.Equal(490m, fixture.YooKassa.LastChargeRequest!.AmountRub);
    }

    /// <summary>The missing-price guard mirrors the seat-pricing one exactly: a subscription that
    /// really did purchase extra Administrators must never renew at a silently-lower price because
    /// `admin-extra` disappeared from the catalog between the purchase and this renewal - the identical
    /// "unreachable in a correctly configured deployment, thrown rather than translated" posture
    /// `basePrice`/`extraPrice` already use just above.</summary>
    [Fact]
    public async Task HandleAsync_WithPurchasedExtraAdministrators_ButNoAdminPricePublished_ThrowsRatherThanChargingZero()
    {
        var subscriptionId = new BillingSubscriptionId(Guid.NewGuid());
        var createdAt = Now - BillingSubscription.PeriodLength;
        var subscription = BillingSubscription.Create(
            subscriptionId, SiteId, "pmt_base", requestedSeats: 3, tier: SubscriptionTierBands.Starter,
            baseSeatPriceVersion: 1, extraSeatPriceVersion: 1, createdAt);
        subscription.MarkSucceeded("card_on_file", createdAt);

        var subscriptions = new FakeBillingSubscriptionRepository();
        var prices = new FakePriceCatalogRepository();
        prices.SeedVersion(SubscriptionTierBands.BaseSeatPriceKey, 490m, createdAt);
        prices.SeedVersion(SubscriptionTierBands.ExtraSeatPriceKey, 200m, createdAt);
        // The purchase happened under some past version (its exact sequence is irrelevant here - this
        // renewal never calls FindVersionAsync, only FindCurrentAsync, the identical "read fresh, never
        // the subscription's own stored one" discipline `basePrice`/`extraPrice` use above). `admin-extra`
        // is deliberately never seeded into this catalog at all - unpublished by renewal time is exactly
        // this test's fixture.
        subscription.ApplyAdministratorPurchase(newExtraAdministratorCount: 1, adminExtraPriceVersion: 1);
        subscriptions.Seed(subscription);

        var site = new Site(SiteId, "pk-" + SiteId.Value, allowedOrigins: [], tier: SubscriptionTierBands.Starter);
        var sites = new FakeSiteRepository();
        sites.Seed(site);

        var thresholds = new FakeDownloadThresholdReadStore();
        var overageReads = new FakeDownloadOverageReadStore();
        var yooKassa = new FakeYooKassaPaymentsClient();
        var applier = new FakeSubscriptionRenewalApplier();
        var handler = new ProcessSubscriptionRenewalHandler(
            subscriptions, sites, yooKassa, prices, thresholds, overageReads, applier, new FakeClock(Now));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(
                new Application.UseCases.ProcessSubscriptionRenewal.ProcessSubscriptionRenewal(subscriptionId),
                CancellationToken.None));

        Assert.Null(yooKassa.LastChargeRequest);
        Assert.Empty(applier.RenewedSuccessfully);
    }
}
