using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.PurchaseChannelAddOn;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.PurchaseChannelAddOn;

public class PurchaseChannelAddOnHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    // `docs/backlog/26-278-*.md`'s own real, published figure - +100 rub/mo per connected channel,
    // flat, independent of ChannelKind.
    private const decimal ChannelAddOnPriceRub = 100m;

    private sealed record Fixture(
        PurchaseChannelAddOnHandler Handler,
        FakeBillingSubscriptionRepository Subscriptions,
        FakeYooKassaPaymentsClient YooKassa,
        FakeChannelAddOnPurchaseApplier Applier,
        FakePriceCatalogRepository Prices,
        FakePermissionChecker Permissions);

    private static Fixture CreateFixture(bool seedPrice = true, bool grantPermission = true)
    {
        var subscriptions = new FakeBillingSubscriptionRepository();
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        }

        var yooKassa = new FakeYooKassaPaymentsClient();
        var applier = new FakeChannelAddOnPurchaseApplier();
        var prices = new FakePriceCatalogRepository();
        if (seedPrice)
        {
            prices.SeedVersion(ChannelAddOnPricing.ChannelAddOnKey, ChannelAddOnPriceRub, Now);
        }

        var handler = new PurchaseChannelAddOnHandler(
            subscriptions, permissions, yooKassa, prices, applier, new FakeIdGenerator(), new FakeClock(Now));

        return new Fixture(handler, subscriptions, yooKassa, applier, prices, permissions);
    }

    private static BillingSubscription SeedSucceededBaseAsync(
        Fixture fixture, BillingSubscriptionId id, DateTimeOffset? succeededAt = null)
    {
        var when = succeededAt ?? Now - BillingSubscription.PeriodLength;
        var subscription = BillingSubscription.Create(
            id, SiteId, "pmt_base", requestedSeats: 5, SubscriptionTierBands.Starter, baseSeatPriceVersion: 1, extraSeatPriceVersion: 1, when);
        subscription.MarkSucceeded("card_abc", when);
        fixture.Subscriptions.Seed(subscription);
        return subscription;
    }

    // `26-278`'s own Done-when: the charge matches 0012's own +100 rub/mo per connected channel - the
    // first (and only) purchase for a given kind, prorated for half the billing period.
    [Fact]
    public async Task HandleAsync_WhenTheBaseIsHalfwayThroughItsPeriod_ChargesHalfTheFlatRate_AndAppliesTheGrant()
    {
        var fixture = CreateFixture();
        var baseId = new BillingSubscriptionId(Guid.NewGuid());
        var halfPeriodAgo = Now - TimeSpan.FromDays(BillingSubscription.PeriodLength.TotalDays / 2);
        var baseSubscription = SeedSucceededBaseAsync(fixture, baseId, succeededAt: halfPeriodAgo);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.PurchaseChannelAddOn.PurchaseChannelAddOn(OperatorId, SiteId, baseId, ChannelKind.Telegram),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Message : null);
        // 100 x 0.5 = 50.00 - never SubscriptionTierBands.ComputeSeatPriceRub's own banded shape, which
        // does not apply here, and no "old price" to net against (this handler's own remarks).
        Assert.Equal(50.00m, result.Value.ProratedAmountRub);
        Assert.Equal(ChannelKind.Telegram, result.Value.ChannelKind);
        Assert.NotNull(fixture.YooKassa.LastChargeRequest);
        Assert.Equal(50.00m, fixture.YooKassa.LastChargeRequest!.AmountRub);

        Assert.Single(fixture.Applier.Applied);
        var applied = fixture.Applier.Applied[0];
        Assert.Equal(SiteId, applied.SiteId);
        Assert.Equal(ChannelKind.Telegram, applied.ChannelKind);
        Assert.Equal(new BillingOptionKey("channel-telegram"), applied.OptionKey);
        Assert.Equal(baseSubscription.CurrentPeriodEnd, applied.AlignedPeriodEnd);
        Assert.Equal(baseSubscription.PaymentMethodId, applied.PaymentMethodId);
        Assert.Equal(result.Value.OptionSubscriptionId, applied.NewOptionId);
    }

    [Fact]
    public async Task HandleAsync_WhenNoPriceIsConfiguredForTheChannelAddOnKey_ReturnsPriceNotConfigured()
    {
        var fixture = CreateFixture(seedPrice: false);
        var baseId = new BillingSubscriptionId(Guid.NewGuid());
        SeedSucceededBaseAsync(fixture, baseId);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.PurchaseChannelAddOn.PurchaseChannelAddOn(OperatorId, SiteId, baseId, ChannelKind.Telegram),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Billing.PriceNotConfigured", result.Error!.Value.Code);
        Assert.Null(fixture.YooKassa.LastChargeRequest);
        Assert.Empty(fixture.Applier.Applied);
    }

    [Fact]
    public async Task HandleAsync_WhenTheBaseSubscriptionIsNotSucceeded_ReturnsNotActive()
    {
        var fixture = CreateFixture();
        var baseId = new BillingSubscriptionId(Guid.NewGuid());
        var pending = BillingSubscription.Create(
            baseId, SiteId, "pmt_pending", requestedSeats: 5, SubscriptionTierBands.Starter, 1, 1, Now - BillingSubscription.PeriodLength);
        fixture.Subscriptions.Seed(pending);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.PurchaseChannelAddOn.PurchaseChannelAddOn(OperatorId, SiteId, baseId, ChannelKind.Telegram),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Billing.SubscriptionNotActive", result.Error!.Value.Code);
        Assert.Null(fixture.YooKassa.LastChargeRequest);
        Assert.Empty(fixture.Applier.Applied);
    }

    // `26-278`'s own defensive guard: the endpoint names a *base* subscription id - an option row named
    // instead (a caller mistake, since an option has no period of its own worth aligning a second option
    // to) is refused rather than silently accepted.
    [Fact]
    public async Task HandleAsync_WhenTheNamedSubscriptionIsItselfAnOption_ReturnsNotActive()
    {
        var fixture = CreateFixture();
        var optionId = new BillingSubscriptionId(Guid.NewGuid());
        var option = BillingSubscription.CreateOption(
            optionId, SiteId, "pmt_option", new BillingOptionKey("channel-telegram"), Now - BillingSubscription.PeriodLength);
        option.MarkSucceeded("card_option", Now - BillingSubscription.PeriodLength, alignedPeriodEnd: Now);
        fixture.Subscriptions.Seed(option);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.PurchaseChannelAddOn.PurchaseChannelAddOn(OperatorId, SiteId, optionId, ChannelKind.Max),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Billing.SubscriptionNotActive", result.Error!.Value.Code);
        Assert.Empty(fixture.Applier.Applied);
    }

    [Fact]
    public async Task HandleAsync_WhenCallerLacksSiteConfigurePermission_ReturnsForbidden()
    {
        var fixture = CreateFixture(grantPermission: false);
        var baseId = new BillingSubscriptionId(Guid.NewGuid());
        SeedSucceededBaseAsync(fixture, baseId);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.PurchaseChannelAddOn.PurchaseChannelAddOn(OperatorId, SiteId, baseId, ChannelKind.Telegram),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Null(fixture.YooKassa.LastChargeRequest);
    }

    [Fact]
    public async Task HandleAsync_WhenTheBaseSubscriptionIsNotFound_ReturnsNotFound()
    {
        var fixture = CreateFixture();
        var missingId = new BillingSubscriptionId(Guid.NewGuid());

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.PurchaseChannelAddOn.PurchaseChannelAddOn(OperatorId, SiteId, missingId, ChannelKind.Telegram),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Billing.SubscriptionNotFound", result.Error!.Value.Code);
    }
}
