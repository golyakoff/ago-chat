using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.PreviewBillingPurchase;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.PreviewBillingPurchase;

/// <summary>
/// `26-299`: proves the preview's own headline promise - "preview and charge can never honestly
/// disagree" (<c>PreviewBillingPurchase</c>'s own remarks) - by asserting the exact same figures
/// <c>PurchaseAdministratorSlotHandlerTests</c>/<c>ChangeSubscriptionSeatsHandlerTests</c>/
/// <c>PurchaseChannelAddOnHandlerTests</c> already prove their real purchase handlers charge, computed
/// here by a read that never calls <see cref="Ago.Chat.Application.Abstractions.IYooKassaPaymentsClient"/>
/// at all.
/// </summary>
public class PreviewBillingPurchaseHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        PreviewBillingPurchaseHandler Handler, FakeBillingSubscriptionRepository Subscriptions, FakePriceCatalogRepository Prices,
        FakePermissionChecker Permissions);

    private static Fixture CreateFixture(bool grantPermission = true)
    {
        var subscriptions = new FakeBillingSubscriptionRepository();
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        }

        var prices = new FakePriceCatalogRepository();
        prices.SeedVersion(SubscriptionTierBands.BaseSeatPriceKey, 490m, Now);
        prices.SeedVersion(SubscriptionTierBands.ExtraSeatPriceKey, 200m, Now);

        var handler = new PreviewBillingPurchaseHandler(subscriptions, permissions, prices, new FakeClock(Now));
        return new Fixture(handler, subscriptions, prices, permissions);
    }

    private static BillingSubscription SeedSucceededBase(
        Fixture fixture, int seats = 3, int extraAdministrators = 0, DateTimeOffset? succeededAt = null)
    {
        var when = succeededAt ?? Now - TimeSpan.FromDays(BillingSubscription.PeriodLength.TotalDays / 2);
        var subscription = BillingSubscription.Create(
            new BillingSubscriptionId(Guid.NewGuid()), SiteId, "pmt_base", seats, SubscriptionTierBands.Starter,
            baseSeatPriceVersion: 1, extraSeatPriceVersion: 1, when);
        subscription.MarkSucceeded("card_abc", when);
        if (extraAdministrators > 0)
        {
            subscription.ApplyAdministratorPurchase(extraAdministrators, adminExtraPriceVersion: 1);
        }

        fixture.Subscriptions.Seed(subscription);
        return subscription;
    }

    [Fact]
    public async Task HandleAsync_ForSeats_MatchesWhatChangeSubscriptionSeatsHandlerWouldActuallyCharge()
    {
        var fixture = CreateFixture();
        // Half the period elapsed - the identical fixture shape ChangeSubscriptionSeatsHandlerTests'
        // own "3 -> 5 seats, halfway through the period" test uses, charging 100.00 there.
        var subscription = SeedSucceededBase(fixture, seats: 3);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.PreviewBillingPurchase.PreviewBillingPurchase(
                OperatorId, SiteId, subscription.Id, BillingPurchaseKind.Seats, RequestedSeats: 5, null, null),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Message : null);
        // old (3 seats, at the base) = 490; new (5 seats, 2 past the base) = 490 + 2x200 = 890;
        // (890 - 490) x 0.5 = 200.00
        Assert.Equal(200.00m, result.Value.ChargedNowRub);
        Assert.Equal(400m, result.Value.ThenRecurringRub);
        Assert.Equal(subscription.CurrentPeriodEnd, result.Value.IncludedUntil);
    }

    [Fact]
    public async Task HandleAsync_ForSeats_OnDayOneOfThePeriod_ChargesTheFullDelta()
    {
        var fixture = CreateFixture();
        var subscription = SeedSucceededBase(fixture, seats: 3, succeededAt: Now - TimeSpan.FromSeconds(3));

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.PreviewBillingPurchase.PreviewBillingPurchase(
                OperatorId, SiteId, subscription.Id, BillingPurchaseKind.Seats, RequestedSeats: 5, null, null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(400.00m, result.Value.ChargedNowRub);
    }

    [Fact]
    public async Task HandleAsync_ForAdministrators_MatchesWhatPurchaseAdministratorSlotHandlerWouldActuallyCharge()
    {
        var fixture = CreateFixture();
        fixture.Prices.SeedVersion(SubscriptionTierBands.AdminExtraPriceKey, 500m, Now);
        var subscription = SeedSucceededBase(fixture);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.PreviewBillingPurchase.PreviewBillingPurchase(
                OperatorId, SiteId, subscription.Id, BillingPurchaseKind.Administrators, null, RequestedExtraAdministrators: 1, null),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Message : null);
        Assert.Equal(250.00m, result.Value.ChargedNowRub); // (500 - 0) x 0.5
        Assert.Equal(500m, result.Value.ThenRecurringRub);
    }

    [Fact]
    public async Task HandleAsync_ForChannel_MatchesWhatPurchaseChannelAddOnHandlerWouldActuallyCharge()
    {
        var fixture = CreateFixture();
        fixture.Prices.SeedVersion(ChannelAddOnPricing.ChannelAddOnKey, 100m, Now);
        var subscription = SeedSucceededBase(fixture);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.PreviewBillingPurchase.PreviewBillingPurchase(
                OperatorId, SiteId, subscription.Id, BillingPurchaseKind.Channel, null, null, ChannelKind.Telegram),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Message : null);
        Assert.Equal(50.00m, result.Value.ChargedNowRub); // 100 x 0.5
        Assert.Equal(100m, result.Value.ThenRecurringRub);
    }

    [Fact]
    public async Task HandleAsync_ForChannel_WhenAlreadyConnected_ReturnsChannelAlreadyConnected()
    {
        var fixture = CreateFixture();
        fixture.Prices.SeedVersion(ChannelAddOnPricing.ChannelAddOnKey, 100m, Now);
        var subscription = SeedSucceededBase(fixture);
        var channel = BillingSubscription.CreateOption(
            new BillingSubscriptionId(Guid.NewGuid()), SiteId, "pmt_channel", new BillingOptionKey("channel-telegram"), Now);
        channel.MarkSucceeded("card_abc", Now, alignedPeriodEnd: subscription.CurrentPeriodEnd);
        fixture.Subscriptions.Seed(channel);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.PreviewBillingPurchase.PreviewBillingPurchase(
                OperatorId, SiteId, subscription.Id, BillingPurchaseKind.Channel, null, null, ChannelKind.Telegram),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Billing.ChannelAlreadyConnected", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_ForSeats_WithNoRequestedSeats_ReturnsPreviewRequestInvalid()
    {
        var fixture = CreateFixture();
        var subscription = SeedSucceededBase(fixture);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.PreviewBillingPurchase.PreviewBillingPurchase(
                OperatorId, SiteId, subscription.Id, BillingPurchaseKind.Seats, null, null, null),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Billing.PreviewRequestInvalid", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenCallerLacksSiteConfigurePermission_ReturnsForbidden()
    {
        var fixture = CreateFixture(grantPermission: false);
        var subscription = SeedSucceededBase(fixture);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.PreviewBillingPurchase.PreviewBillingPurchase(
                OperatorId, SiteId, subscription.Id, BillingPurchaseKind.Seats, 5, null, null),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenTheSubscriptionDoesNotExistForThisSite_ReturnsSubscriptionNotFound()
    {
        var fixture = CreateFixture();
        var missingId = new BillingSubscriptionId(Guid.NewGuid());

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.PreviewBillingPurchase.PreviewBillingPurchase(
                OperatorId, SiteId, missingId, BillingPurchaseKind.Seats, 5, null, null),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Billing.SubscriptionNotFound", result.Error!.Value.Code);
    }
}
