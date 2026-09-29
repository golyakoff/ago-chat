using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.CreateTokenPayment;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.CreateTokenPayment;

/// <summary>`26-291`: `CreateCheckoutSessionHandlerTests`' own sibling for the SDK token-payment flow -
/// deliberately proves only what actually differs (the token itself, the `Status`/`ConfirmationUrl`
/// shape of the result), leaning on `CreateCheckoutSessionHandlerTests` to already prove the shared seat
/// validation and price-catalog behaviour identically.</summary>
public class CreateTokenPaymentHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        CreateTokenPaymentHandler Handler, FakeBillingSubscriptionRepository Subscriptions, FakeYooKassaPaymentsClient YooKassa);

    private static Fixture CreateFixture(bool grantPermission = true, decimal baseSeatPriceRub = 490m, decimal pricePerExtraSeatRub = 200m)
    {
        var sites = new FakeSiteRepository();
        sites.Seed(new Site(SiteId, "shop_token", []));
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        }

        var subscriptions = new FakeBillingSubscriptionRepository();
        var yooKassa = new FakeYooKassaPaymentsClient();
        var prices = new FakePriceCatalogRepository();
        prices.SeedVersion(SubscriptionTierBands.BaseSeatPriceKey, baseSeatPriceRub, Now);
        prices.SeedVersion(SubscriptionTierBands.ExtraSeatPriceKey, pricePerExtraSeatRub, Now);

        var handler = new CreateTokenPaymentHandler(sites, permissions, subscriptions, yooKassa, prices, new FakeIdGenerator(), new FakeClock(Now));

        return new Fixture(handler, subscriptions, yooKassa);
    }

    [Fact]
    public async Task HandleAsync_WhenTokenPaymentComesBackPending_CreatesAPendingSubscription_AndReturnsTheConfirmationUrl()
    {
        var fixture = CreateFixture();
        fixture.YooKassa.TokenResult = new CreatePaymentWithTokenResult.Success("pmt_token_1", "pending", "https://yookassa.example/confirm-sdk");

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.CreateTokenPayment.CreateTokenPayment(OperatorId, SiteId, 5, "tok_abc123", SavePaymentMethod: true), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Message : null);
        Assert.Equal("pending", result.Value.Status);
        Assert.Equal("https://yookassa.example/confirm-sdk", result.Value.ConfirmationUrl);

        var saved = Assert.Single(fixture.Subscriptions.Saved);
        Assert.Equal(SiteId, saved.SiteId);
        Assert.Equal("pmt_token_1", saved.YooKassaPaymentId);
        Assert.Equal(5, saved.RequestedSeats);
        Assert.Equal(SubscriptionTierBands.Starter, saved.Tier);
        // `13-02`'s own "never the redirect alone" discipline - the row is Pending even though the token
        // payment already carries a real ЮKassa status, per CreateTokenPaymentHandler's own remarks.
        Assert.Equal(BillingSubscriptionStatus.Pending, saved.Status);
    }

    // The "captured outright" case - ЮKassa answers already `succeeded`, with no confirmation_url at all
    // (no 3DS/SberPay step needed). The subscription is still recorded Pending; only the webhook grants.
    [Fact]
    public async Task HandleAsync_WhenTokenPaymentCapturesOutright_ReturnsSucceededStatus_WithNoConfirmationUrl()
    {
        var fixture = CreateFixture();
        fixture.YooKassa.TokenResult = new CreatePaymentWithTokenResult.Success("pmt_token_2", "succeeded", ConfirmationUrl: null);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.CreateTokenPayment.CreateTokenPayment(OperatorId, SiteId, 3, "tok_def456", SavePaymentMethod: true), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Message : null);
        Assert.Equal("succeeded", result.Value.Status);
        Assert.Null(result.Value.ConfirmationUrl);
        var saved = Assert.Single(fixture.Subscriptions.Saved);
        Assert.Equal(BillingSubscriptionStatus.Pending, saved.Status);
    }

    [Fact]
    public async Task HandleAsync_PassesTheTokenAndComputedAmount_ToTheYooKassaPort()
    {
        var fixture = CreateFixture(baseSeatPriceRub: 490m, pricePerExtraSeatRub: 200m);

        await fixture.Handler.HandleAsync(
            new Application.UseCases.CreateTokenPayment.CreateTokenPayment(OperatorId, SiteId, 4, "tok_the_one", SavePaymentMethod: true), CancellationToken.None);

        Assert.NotNull(fixture.YooKassa.LastTokenRequest);
        Assert.Equal("tok_the_one", fixture.YooKassa.LastTokenRequest!.PaymentToken);
        // 4 seats: SubscriptionTierBands.BaseSeats (3) covered by the base price, one extra seat.
        Assert.Equal(690m, fixture.YooKassa.LastTokenRequest.AmountRub);
        // The redirect flow's own port method must never be called by the token path.
        Assert.Null(fixture.YooKassa.LastRequest);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task HandleAsync_WhenThePaymentTokenIsBlank_ReturnsInvalidPaymentToken_AndNeverCallsYooKassa(string? blankToken)
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.CreateTokenPayment.CreateTokenPayment(OperatorId, SiteId, 5, blankToken!, SavePaymentMethod: true), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Billing.InvalidPaymentToken", result.Error!.Value.Code);
        Assert.Null(fixture.YooKassa.LastTokenRequest);
        Assert.Empty(fixture.Subscriptions.Saved);
    }

    [Fact]
    public async Task HandleAsync_WhenTheOperatorLacksSiteConfigure_ReturnsForbidden_AndCallsNeitherYooKassaNorSaves()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.CreateTokenPayment.CreateTokenPayment(OperatorId, SiteId, 5, "tok_abc", SavePaymentMethod: true), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Null(fixture.YooKassa.LastTokenRequest);
        Assert.Empty(fixture.Subscriptions.Saved);
    }

    [Fact]
    public async Task HandleAsync_WhenSeatsAreOutsideTheBandTable_ReturnsInvalidSeatCount_AndNeverCallsYooKassa()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.CreateTokenPayment.CreateTokenPayment(OperatorId, SiteId, 1, "tok_abc", SavePaymentMethod: true), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Billing.InvalidSeatCount", result.Error!.Value.Code);
        Assert.Null(fixture.YooKassa.LastTokenRequest);
    }

    [Fact]
    public async Task HandleAsync_WhenYooKassaRefusesTheToken_ReturnsPaymentProviderRefused_AndSavesNoSubscription()
    {
        var fixture = CreateFixture();
        fixture.YooKassa.TokenResult = new CreatePaymentWithTokenResult.Refused("expired token");

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.CreateTokenPayment.CreateTokenPayment(OperatorId, SiteId, 5, "tok_expired", SavePaymentMethod: true), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Billing.PaymentProviderRefused", result.Error!.Value.Code);
        Assert.Empty(fixture.Subscriptions.Saved);
    }

    [Fact]
    public async Task HandleAsync_WhenEitherSeatPricingKeyHasNoPublishedVersion_ReturnsPriceNotConfigured_AndNeverCallsYooKassaOrSaves()
    {
        var sites = new FakeSiteRepository();
        sites.Seed(new Site(SiteId, "shop_token", []));
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        var subscriptions = new FakeBillingSubscriptionRepository();
        var yooKassa = new FakeYooKassaPaymentsClient();
        var prices = new FakePriceCatalogRepository();
        var handler = new CreateTokenPaymentHandler(sites, permissions, subscriptions, yooKassa, prices, new FakeIdGenerator(), new FakeClock(Now));

        var result = await handler.HandleAsync(
            new Application.UseCases.CreateTokenPayment.CreateTokenPayment(OperatorId, SiteId, 5, "tok_abc", SavePaymentMethod: true), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Billing.PriceNotConfigured", result.Error!.Value.Code);
        Assert.Null(yooKassa.LastTokenRequest);
        Assert.Empty(subscriptions.Saved);
    }
}
