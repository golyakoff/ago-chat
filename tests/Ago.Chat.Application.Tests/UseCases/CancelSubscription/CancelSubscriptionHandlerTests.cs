using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.CancelSubscription;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.CancelSubscription;

public class CancelSubscriptionHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        Application.UseCases.CancelSubscription.CancelSubscriptionHandler Handler, FakeBillingSubscriptionRepository Subscriptions);

    private static Fixture CreateFixture(bool grantPermission = true)
    {
        var subscriptions = new FakeBillingSubscriptionRepository();
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        }

        var handler = new Application.UseCases.CancelSubscription.CancelSubscriptionHandler(subscriptions, permissions, new FakeClock(Now));
        return new Fixture(handler, subscriptions);
    }

    private static BillingSubscription SeedSucceeded(FakeBillingSubscriptionRepository subscriptions, BillingSubscriptionId id)
    {
        var subscription = BillingSubscription.Create(id, SiteId, "pmt_123", 5, SubscriptionTierBands.Starter, Now - BillingSubscription.PeriodLength);
        subscription.MarkSucceeded("card_abc", Now - BillingSubscription.PeriodLength);
        subscriptions.Seed(subscription);
        return subscription;
    }

    [Fact]
    public async Task HandleAsync_WhenNotPermitted_ReturnsForbidden()
    {
        var fixture = CreateFixture(grantPermission: false);
        var id = new BillingSubscriptionId(Guid.NewGuid());
        SeedSucceeded(fixture.Subscriptions, id);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.CancelSubscription.CancelSubscription(OperatorId, SiteId, id), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenNoSuchSubscription_ReturnsNotFound()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.CancelSubscription.CancelSubscription(OperatorId, SiteId, new BillingSubscriptionId(Guid.NewGuid())),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Billing.SubscriptionNotFound", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenSucceeded_SetsCancelRequestedAndReturnsThePaidThroughDate()
    {
        var fixture = CreateFixture();
        var id = new BillingSubscriptionId(Guid.NewGuid());
        var subscription = SeedSucceeded(fixture.Subscriptions, id);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.CancelSubscription.CancelSubscription(OperatorId, SiteId, id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(subscription.CurrentPeriodEnd, result.Value.PaidThroughUntil);
        Assert.True(subscription.CancelRequested);
        Assert.Single(fixture.Subscriptions.Updated);
    }

    [Fact]
    public async Task HandleAsync_WhenAlreadyLapsed_ReturnsNotActive()
    {
        var fixture = CreateFixture();
        var id = new BillingSubscriptionId(Guid.NewGuid());
        var subscription = SeedSucceeded(fixture.Subscriptions, id);
        subscription.RecordRenewalFailure(Now);
        subscription.MarkLapsed();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.CancelSubscription.CancelSubscription(OperatorId, SiteId, id), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Billing.SubscriptionNotActive", result.Error!.Value.Code);
    }
}
