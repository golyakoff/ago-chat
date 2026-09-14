using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.CreateCheckoutSession;
using Ago.Chat.Application.UseCases.PurchaseDownloadOverage;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.PurchaseDownloadOverage;

/// <summary>`25-84`: the manual path's own checkout - the first purchase in this codebase whose amount
/// is computed at the moment of payment rather than chosen from a menu.</summary>
public class PurchaseDownloadOverageHandlerTests
{
    private const long OneGibibyte = 1024L * 1024L * 1024L;
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly PeriodMonth = new(2026, 9, 1);

    private sealed record Fixture(
        PurchaseDownloadOverageHandler Handler,
        FakeYooKassaPaymentsClient YooKassa,
        FakeDownloadOverageChargeRepository Charges,
        FakeAttachmentEgressReadStore EgressReads,
        FakeDownloadThresholdReadStore Thresholds,
        FakeDownloadOverageReadStore OverageReads,
        FakePriceCatalogRepository Prices,
        FakePermissionChecker Permissions);

    private static Fixture CreateFixture(bool grantPermission = true)
    {
        var sites = new FakeSiteRepository();
        sites.Seed(new Site(SiteId, "pk-" + SiteId.Value, allowedOrigins: []));

        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        }

        var egressReads = new FakeAttachmentEgressReadStore();
        var thresholds = new FakeDownloadThresholdReadStore();
        var overageReads = new FakeDownloadOverageReadStore();
        var charges = new FakeDownloadOverageChargeRepository();
        var prices = new FakePriceCatalogRepository();
        var yooKassa = new FakeYooKassaPaymentsClient();

        var handler = new PurchaseDownloadOverageHandler(
            sites, permissions, egressReads, thresholds, overageReads, charges, prices, yooKassa,
            new BillingOptions { CheckoutReturnUrl = "https://console.example/billing" },
            new FakeIdGenerator(), new FakeClock(Now));

        return new Fixture(handler, yooKassa, charges, egressReads, thresholds, overageReads, prices, permissions);
    }

    /// <summary>Seeds a tenant sitting `gibibytesOver` past a one-gibibyte hard threshold, with the
    /// shipped 100 RUB/GiB price published.</summary>
    private static void SeedOverBy(Fixture fixture, decimal gibibytesOver)
    {
        fixture.Thresholds.Seed("free", softThresholdBytes: OneGibibyte / 2, hardThresholdBytes: OneGibibyte);
        fixture.EgressReads.SeedBytesOut(SiteId, PeriodMonth, OneGibibyte + (long)(OneGibibyte * gibibytesOver));
        fixture.Prices.SeedVersion(DownloadOveragePricing.OveragePerGigabyteKey, 100m, Now);
    }

    /// <summary>`docs/backlog/25-84-*.md`'s own Done-when: "a charge computed from real
    /// gigabytes-over-threshold... provably correct against a fabricated egress figure - not merely
    /// 'some amount was charged'." 2.5 GiB over at 100 RUB/GiB is exactly 250 RUB, and that exact figure
    /// is what reaches the payment provider.</summary>
    [Fact]
    public async Task HandleAsync_ChargesTheRealGigabytesOverTheThreshold_AtThePublishedPrice()
    {
        var fixture = CreateFixture();
        SeedOverBy(fixture, gibibytesOver: 2.5m);

        var result = await fixture.Handler.HandleAsync(
            new global::Ago.Chat.Application.UseCases.PurchaseDownloadOverage.PurchaseDownloadOverage(OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(250.00m, result.Value.AmountRub);
        Assert.Equal((long)(OneGibibyte * 2.5m), result.Value.BytesOver);
        Assert.Equal(250.00m, fixture.YooKassa.LastRequest!.AmountRub);
        Assert.Equal("https://console.example/billing", fixture.YooKassa.LastRequest.ReturnUrl);
    }

    /// <summary>The same purchase at a price the platform owner republished - nothing is hardcoded, so
    /// the amount follows. 2 GiB over at 250 RUB/GiB is 500 RUB.</summary>
    [Fact]
    public async Task HandleAsync_FollowsThePlatformOwnersOwnCurrentPrice_NotTheShippedDefault()
    {
        var fixture = CreateFixture();
        fixture.Thresholds.Seed("free", softThresholdBytes: OneGibibyte / 2, hardThresholdBytes: OneGibibyte);
        fixture.EgressReads.SeedBytesOut(SiteId, PeriodMonth, 3 * OneGibibyte);
        fixture.Prices.SeedVersion(DownloadOveragePricing.OveragePerGigabyteKey, 100m, Now);
        fixture.Prices.SeedVersion(DownloadOveragePricing.OveragePerGigabyteKey, 250m, Now);

        var result = await fixture.Handler.HandleAsync(
            new global::Ago.Chat.Application.UseCases.PurchaseDownloadOverage.PurchaseDownloadOverage(OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(500.00m, result.Value.AmountRub);
    }

    /// <summary>Only the *unsettled* remainder is charged - a tenant who already paid for two of their
    /// three gibibytes over owes one, not three. This is what stops a second checkout in the same month
    /// from double-charging for bytes already paid for.</summary>
    [Fact]
    public async Task HandleAsync_ChargesOnlyWhatIsNotAlreadySettled()
    {
        var fixture = CreateFixture();
        SeedOverBy(fixture, gibibytesOver: 3m);
        fixture.OverageReads.SeedCharge(SiteId, PeriodMonth, bytesOver: 2 * OneGibibyte, amountRub: 200m);

        var result = await fixture.Handler.HandleAsync(
            new global::Ago.Chat.Application.UseCases.PurchaseDownloadOverage.PurchaseDownloadOverage(OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(100.00m, result.Value.AmountRub);
    }

    /// <summary>The purchase records a <c>Pending</c> checkout row carrying ЮKassa's own payment id and
    /// the price version the amount came from - never a settled one. Nothing about this call unblocks
    /// the tenant; only the webhook does.</summary>
    [Fact]
    public async Task HandleAsync_RecordsAPendingCheckoutCharge_NeverASettledOne()
    {
        var fixture = CreateFixture();
        SeedOverBy(fixture, gibibytesOver: 1m);

        await fixture.Handler.HandleAsync(
            new global::Ago.Chat.Application.UseCases.PurchaseDownloadOverage.PurchaseDownloadOverage(OperatorId, SiteId),
            CancellationToken.None);

        var charge = Assert.Single(fixture.Charges.Saved);
        Assert.Equal(DownloadOverageChargeStatus.Pending, charge.Status);
        Assert.Equal(DownloadOverageChargeSource.Checkout, charge.Source);
        Assert.Equal("pmt_fake", charge.YooKassaPaymentId);
        Assert.Equal(PeriodMonth, charge.PeriodMonth);
        Assert.Equal(OneGibibyte, charge.BytesOver);
        Assert.Equal(100.00m, charge.AmountRub);
        Assert.Equal(1, charge.PriceVersion);
        Assert.Null(charge.SettledAt);
    }

    /// <summary>A tenant who is not over the threshold has nothing to buy - refused as a domain error
    /// before any outbound call, never a `0 RUB` payment the provider would reject with a worse
    /// message.</summary>
    [Fact]
    public async Task HandleAsync_WhenNotOverTheThreshold_RefusesWithoutCallingTheProvider()
    {
        var fixture = CreateFixture();
        fixture.Thresholds.Seed("free", softThresholdBytes: OneGibibyte / 2, hardThresholdBytes: OneGibibyte);
        fixture.EgressReads.SeedBytesOut(SiteId, PeriodMonth, OneGibibyte - 1);
        fixture.Prices.SeedVersion(DownloadOveragePricing.OveragePerGigabyteKey, 100m, Now);

        var result = await fixture.Handler.HandleAsync(
            new global::Ago.Chat.Application.UseCases.PurchaseDownloadOverage.PurchaseDownloadOverage(OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Attachment.DownloadOverageNothingToPay", result.Error!.Value.Code);
        Assert.Null(fixture.YooKassa.LastRequest);
        Assert.Empty(fixture.Charges.Saved);
    }

    /// <summary>A deployment that never published a per-gigabyte price refuses cleanly - `25-43`'s own
    /// "built, not yet for sale" state, reached through a checkout route rather than a charge site, and
    /// never a charge computed from a missing price as if it were zero.</summary>
    [Fact]
    public async Task HandleAsync_WhenNoPriceWasEverPublished_RefusesBeforeAnyOutboundCall()
    {
        var fixture = CreateFixture();
        fixture.Thresholds.Seed("free", softThresholdBytes: OneGibibyte / 2, hardThresholdBytes: OneGibibyte);
        fixture.EgressReads.SeedBytesOut(SiteId, PeriodMonth, 3 * OneGibibyte);

        var result = await fixture.Handler.HandleAsync(
            new global::Ago.Chat.Application.UseCases.PurchaseDownloadOverage.PurchaseDownloadOverage(OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Billing.PriceNotConfigured", result.Error!.Value.Code);
        Assert.Null(fixture.YooKassa.LastRequest);
    }

    /// <summary>A provider refusal is the caller's own outcome, not a half-applied state - no charge row
    /// is recorded for a payment that was never created.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheProviderRefuses_RecordsNothing()
    {
        var fixture = CreateFixture();
        SeedOverBy(fixture, gibibytesOver: 1m);
        fixture.YooKassa.Result = new CreatePaymentResult.Refused("card declined");

        var result = await fixture.Handler.HandleAsync(
            new global::Ago.Chat.Application.UseCases.PurchaseDownloadOverage.PurchaseDownloadOverage(OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Empty(fixture.Charges.Saved);
    }

    /// <summary>Gated by `site:configure`, the identical permission every other billing write requires -
    /// an operator who cannot configure the site cannot commit it to a charge either.</summary>
    [Fact]
    public async Task HandleAsync_WithoutSiteConfigure_IsForbidden()
    {
        var fixture = CreateFixture(grantPermission: false);
        SeedOverBy(fixture, gibibytesOver: 1m);

        var result = await fixture.Handler.HandleAsync(
            new global::Ago.Chat.Application.UseCases.PurchaseDownloadOverage.PurchaseDownloadOverage(OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Null(fixture.YooKassa.LastRequest);
    }
}
