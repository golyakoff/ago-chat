using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.SetNextPeriodComposition;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.SetNextPeriodComposition;

public class SetNextPeriodCompositionHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        SetNextPeriodCompositionHandler Handler, FakeBillingSubscriptionRepository Subscriptions, FakePermissionChecker Permissions);

    private static Fixture CreateFixture(bool grantPermission = true)
    {
        var subscriptions = new FakeBillingSubscriptionRepository();
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        }

        var handler = new SetNextPeriodCompositionHandler(subscriptions, permissions);
        return new Fixture(handler, subscriptions, permissions);
    }

    private static BillingSubscription SeedSucceededBase(Fixture fixture, int seats = 3, int extraAdministrators = 0)
    {
        var subscription = BillingSubscription.Create(
            new BillingSubscriptionId(Guid.NewGuid()), SiteId, "pmt_base", seats, SubscriptionTierBands.Starter,
            baseSeatPriceVersion: 1, extraSeatPriceVersion: 1, Now - BillingSubscription.PeriodLength);
        subscription.MarkSucceeded("card_abc", Now - BillingSubscription.PeriodLength);
        if (extraAdministrators > 0)
        {
            subscription.ApplyAdministratorPurchase(extraAdministrators, adminExtraPriceVersion: 1);
        }

        fixture.Subscriptions.Seed(subscription);
        return subscription;
    }

    [Fact]
    public async Task HandleAsync_SchedulesTheRequestedSeatsAndAdminsForTheNextPeriod_WithoutTouchingCurrentValues()
    {
        var fixture = CreateFixture();
        var subscription = SeedSucceededBase(fixture, seats: 3, extraAdministrators: 1);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SetNextPeriodComposition.SetNextPeriodComposition(OperatorId, SiteId, subscription.Id, 2, 0),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Message : null);
        Assert.Equal(SubscriptionTierBands.Starter, result.Value.Tier);
        Assert.Equal(2, result.Value.RequestedSeats);
        Assert.Equal(0, result.Value.RequestedExtraAdministrators);

        Assert.Equal(3, subscription.RequestedSeats);
        Assert.Equal(1, subscription.ExtraAdministratorsPurchased);
        Assert.Equal(2, subscription.PendingSeatCount);
        Assert.Equal(0, subscription.PendingAdminCount);
        Assert.Single(fixture.Subscriptions.Updated);
    }

    // Unlike ChangeSubscriptionSeatsHandler's own downgrade-only branch, this endpoint can also schedule
    // an increase - `SetNextPeriodComposition`'s own remarks state why a second entry point exists rather
    // than widening `ScheduleSeatDecrease` in place.
    [Fact]
    public async Task HandleAsync_CanAlsoScheduleAnIncrease()
    {
        var fixture = CreateFixture();
        var subscription = SeedSucceededBase(fixture, seats: 2);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SetNextPeriodComposition.SetNextPeriodComposition(OperatorId, SiteId, subscription.Id, 5, 3),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(5, subscription.PendingSeatCount);
        Assert.Equal(3, subscription.PendingAdminCount);
    }

    [Fact]
    public async Task HandleAsync_WithAnOutOfBandSeatCount_ReturnsInvalidSeatCount()
    {
        var fixture = CreateFixture();
        var subscription = SeedSucceededBase(fixture);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SetNextPeriodComposition.SetNextPeriodComposition(OperatorId, SiteId, subscription.Id, 99, 0),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Billing.InvalidSeatCount", result.Error!.Value.Code);
        Assert.Empty(fixture.Subscriptions.Updated);
    }

    [Fact]
    public async Task HandleAsync_WithANegativeAdminCount_ReturnsInvalidAdministratorCount()
    {
        var fixture = CreateFixture();
        var subscription = SeedSucceededBase(fixture);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SetNextPeriodComposition.SetNextPeriodComposition(OperatorId, SiteId, subscription.Id, 3, -1),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Billing.InvalidAdministratorCount", result.Error!.Value.Code);
        Assert.Empty(fixture.Subscriptions.Updated);
    }

    [Fact]
    public async Task HandleAsync_ForAnOptionSubscription_ReturnsNotActive()
    {
        var fixture = CreateFixture();
        var option = BillingSubscription.CreateOption(
            new BillingSubscriptionId(Guid.NewGuid()), SiteId, "pmt_option", new BillingOptionKey("channel-telegram"), Now - BillingSubscription.PeriodLength);
        option.MarkSucceeded("card_abc", Now - BillingSubscription.PeriodLength, alignedPeriodEnd: Now);
        fixture.Subscriptions.Seed(option);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SetNextPeriodComposition.SetNextPeriodComposition(OperatorId, SiteId, option.Id, 3, 0),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Billing.SubscriptionNotActive", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenCallerLacksSiteConfigurePermission_ReturnsForbidden()
    {
        var fixture = CreateFixture(grantPermission: false);
        var subscription = SeedSucceededBase(fixture);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SetNextPeriodComposition.SetNextPeriodComposition(OperatorId, SiteId, subscription.Id, 3, 0),
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
            new Application.UseCases.SetNextPeriodComposition.SetNextPeriodComposition(OperatorId, SiteId, missingId, 3, 0),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Billing.SubscriptionNotFound", result.Error!.Value.Code);
    }
}
