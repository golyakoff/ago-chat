using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.CreateCheckoutSession;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.CreateCheckoutSession;

public class CreateCheckoutSessionHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        CreateCheckoutSessionHandler Handler, FakeBillingSubscriptionRepository Subscriptions, FakeYooKassaPaymentsClient YooKassa);

    private static Fixture CreateFixture(bool grantPermission = true, decimal pricePerSeat = 500m)
    {
        var sites = new FakeSiteRepository();
        sites.Seed(new Site(SiteId, "shop_7f3a", []));
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        }

        var subscriptions = new FakeBillingSubscriptionRepository();
        var yooKassa = new FakeYooKassaPaymentsClient();
        var billingOptions = new BillingOptions { PricePerSeatRub = pricePerSeat, CheckoutReturnUrl = "https://console.example/billing/return" };

        var handler = new CreateCheckoutSessionHandler(
            sites, permissions, subscriptions, yooKassa, billingOptions, new FakeIdGenerator(), new FakeClock(Now));

        return new Fixture(handler, subscriptions, yooKassa);
    }

    [Fact]
    public async Task HandleAsync_WhenPermittedAndSeatsAreValid_ReturnsTheConfirmationUrl()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.CreateCheckoutSession.CreateCheckoutSession(OperatorId, SiteId, 5), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Message : null);
        Assert.Equal("https://yookassa.example/confirm", result.Value.ConfirmationUrl);
    }

    [Fact]
    public async Task HandleAsync_ComputesAmount_AsSeatsTimesPricePerSeat()
    {
        var fixture = CreateFixture(pricePerSeat: 500m);

        await fixture.Handler.HandleAsync(
            new Application.UseCases.CreateCheckoutSession.CreateCheckoutSession(OperatorId, SiteId, 5), CancellationToken.None);

        Assert.Equal(2500m, fixture.YooKassa.LastRequest!.AmountRub);
    }

    [Fact]
    public async Task HandleAsync_WhenSuccessful_RecordsAPendingSubscriptionWithTheResolvedTier()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(
            new Application.UseCases.CreateCheckoutSession.CreateCheckoutSession(OperatorId, SiteId, 5), CancellationToken.None);

        var saved = Assert.Single(fixture.Subscriptions.Saved);
        Assert.Equal(SiteId, saved.SiteId);
        Assert.Equal("pmt_fake", saved.YooKassaPaymentId);
        Assert.Equal(5, saved.RequestedSeats);
        Assert.Equal(SubscriptionTierBands.Starter, saved.Tier);
        Assert.Equal(BillingSubscriptionStatus.Pending, saved.Status);
    }

    [Fact]
    public async Task HandleAsync_WhenTheOperatorLacksSiteConfigure_ReturnsForbidden_AndCallsNeitherYooKassaNorSaves()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.CreateCheckoutSession.CreateCheckoutSession(OperatorId, SiteId, 5), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Null(fixture.YooKassa.LastRequest);
        Assert.Empty(fixture.Subscriptions.Saved);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(101)]
    public async Task HandleAsync_WhenSeatsAreOutsideTheBandTable_ReturnsInvalidSeatCount_AndNeverCallsYooKassa(int requestedSeats)
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.CreateCheckoutSession.CreateCheckoutSession(OperatorId, SiteId, requestedSeats), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Billing.InvalidSeatCount", result.Error!.Value.Code);
        Assert.Null(fixture.YooKassa.LastRequest);
    }

    [Fact]
    public async Task HandleAsync_WhenYooKassaRefuses_ReturnsPaymentProviderRefused_AndSavesNoSubscription()
    {
        var fixture = CreateFixture();
        fixture.YooKassa.Result = new CreatePaymentResult.Refused("insufficient funds");

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.CreateCheckoutSession.CreateCheckoutSession(OperatorId, SiteId, 5), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Billing.PaymentProviderRefused", result.Error!.Value.Code);
        Assert.Empty(fixture.Subscriptions.Saved);
    }

    [Fact]
    public async Task HandleAsync_WhenSiteDoesNotExist_ReturnsSiteNotFound()
    {
        // A site the caller is permitted on but that ISiteRepository has no row for - the permission
        // check alone cannot distinguish "does not exist" from "not yours", so this seeds the grant
        // against a SiteId no Site was ever seeded for, isolating the SiteNotFound branch from the
        // Forbidden one the test right above already covers.
        var missingSiteId = new SiteId(Guid.NewGuid());
        var extraPermissions = new FakePermissionChecker();
        extraPermissions.Grant(OperatorId, missingSiteId, Permission.SiteConfigure);
        var handler = new CreateCheckoutSessionHandler(
            new FakeSiteRepository(), extraPermissions, new FakeBillingSubscriptionRepository(), new FakeYooKassaPaymentsClient(),
            new BillingOptions { PricePerSeatRub = 500m, CheckoutReturnUrl = "https://console.example/billing/return" },
            new FakeIdGenerator(), new FakeClock(Now));

        var result = await handler.HandleAsync(
            new Application.UseCases.CreateCheckoutSession.CreateCheckoutSession(OperatorId, missingSiteId, 5), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Site.NotFound", result.Error!.Value.Code);
    }
}
