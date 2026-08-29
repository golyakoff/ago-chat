using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetBillingStatus;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetBillingStatus;

/// <summary>`13-04`: the console billing screen's own bootstrap read, proven at the handler level -
/// permission gate, site-not-found, no-subscription-yet, and the honest `Pending` shape a caller
/// returning from ЮKassa's hosted checkout polls against (this file's own last two tests).</summary>
public class GetBillingStatusHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId RequestedBy = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(GetBillingStatusHandler Handler, FakeBillingSubscriptionRepository Subscriptions, FakeOperatorRepository Operators);

    private static Fixture CreateFixture(string tier = "free", int seatLimit = 1, bool grantPermission = true)
    {
        var sites = new FakeSiteRepository();
        sites.Seed(new Site(SiteId, $"site_{SiteId.Value:N}", [], tier: tier, seatLimit: seatLimit));

        var operators = new FakeOperatorRepository();
        var subscriptions = new FakeBillingSubscriptionRepository();

        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(RequestedBy, SiteId, Permission.SiteConfigure);
        }

        var handler = new GetBillingStatusHandler(sites, operators, subscriptions, permissions);
        return new Fixture(handler, subscriptions, operators);
    }

    [Fact]
    public async Task HandleAsync_WhenCallerLacksSiteConfigure_ReturnsForbidden_AndLeaksNoBillingData()
    {
        var fixture = CreateFixture(tier: SubscriptionTierBands.Starter, seatLimit: 5, grantPermission: false);

        var result = await fixture.Handler.HandleAsync(new Application.UseCases.GetBillingStatus.GetBillingStatus(RequestedBy, SiteId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenSiteHasNeverCheckedOut_ReturnsFreeTierWithNoLatestSubscription()
    {
        var fixture = CreateFixture(tier: "free", seatLimit: 1);
        fixture.Operators.Seed(new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5));

        var result = await fixture.Handler.HandleAsync(new Application.UseCases.GetBillingStatus.GetBillingStatus(RequestedBy, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("free", result.Value.Tier);
        Assert.Equal(1, result.Value.SeatLimit);
        Assert.Equal(1, result.Value.SeatsUsed);
        Assert.Null(result.Value.LatestSubscription);
    }

    [Fact]
    public async Task HandleAsync_WhenLatestSubscriptionIsPending_ReportsPendingStatus_NotSucceeded()
    {
        var fixture = CreateFixture(tier: "free", seatLimit: 1);
        var subscription = BillingSubscription.Create(
            new BillingSubscriptionId(Guid.NewGuid()), SiteId, "yk_payment_1", requestedSeats: 5, tier: SubscriptionTierBands.Starter, Now);
        fixture.Subscriptions.Seed(subscription);

        var result = await fixture.Handler.HandleAsync(new Application.UseCases.GetBillingStatus.GetBillingStatus(RequestedBy, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        // The site's own Tier/SeatLimit are untouched - only a verified webhook moves them, never the
        // checkout-session call. A caller must not read this as "confirmed" just because a row exists.
        Assert.Equal("free", result.Value.Tier);
        Assert.Equal(1, result.Value.SeatLimit);
        Assert.NotNull(result.Value.LatestSubscription);
        Assert.Equal("Pending", result.Value.LatestSubscription!.Status);
        Assert.Equal(5, result.Value.LatestSubscription.RequestedSeats);
    }

    [Fact]
    public async Task HandleAsync_WhenLatestSubscriptionHasSucceeded_ReportsSucceededStatus_AndSiteEntitlements()
    {
        var fixture = CreateFixture(tier: SubscriptionTierBands.Starter, seatLimit: 5);
        var subscription = BillingSubscription.Create(
            new BillingSubscriptionId(Guid.NewGuid()), SiteId, "yk_payment_1", requestedSeats: 5, tier: SubscriptionTierBands.Starter, Now);
        subscription.MarkSucceeded("card_abc", Now);
        fixture.Subscriptions.Seed(subscription);

        var result = await fixture.Handler.HandleAsync(new Application.UseCases.GetBillingStatus.GetBillingStatus(RequestedBy, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(SubscriptionTierBands.Starter, result.Value.Tier);
        Assert.Equal(5, result.Value.SeatLimit);
        Assert.Equal("Succeeded", result.Value.LatestSubscription!.Status);
        Assert.False(result.Value.LatestSubscription.CancelRequested);
        Assert.Null(result.Value.LatestSubscription.PendingSeatCount);
    }

    [Fact]
    public async Task HandleAsync_WhenMultipleSubscriptionsExist_ReturnsOnlyTheMostRecentlyCreatedOne()
    {
        var fixture = CreateFixture(tier: "free", seatLimit: 1);
        var older = BillingSubscription.Create(
            new BillingSubscriptionId(Guid.NewGuid()), SiteId, "yk_payment_old", requestedSeats: 3, tier: SubscriptionTierBands.Starter, Now - TimeSpan.FromDays(60));
        older.MarkFailed();
        var newer = BillingSubscription.Create(
            new BillingSubscriptionId(Guid.NewGuid()), SiteId, "yk_payment_new", requestedSeats: 5, tier: SubscriptionTierBands.Starter, Now);
        fixture.Subscriptions.Seed(older);
        fixture.Subscriptions.Seed(newer);

        var result = await fixture.Handler.HandleAsync(new Application.UseCases.GetBillingStatus.GetBillingStatus(RequestedBy, SiteId), CancellationToken.None);

        Assert.Equal(newer.Id.Value, result.Value.LatestSubscription!.SubscriptionId);
        Assert.Equal("Pending", result.Value.LatestSubscription.Status);
    }
}
