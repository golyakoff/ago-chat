using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.OperatorRoleSeats;
using Ago.Chat.Application.UseCases.RenewNow;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Modules;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `26-296`'s own Done-when: "a handler integration test (charge + period extended + idempotent double-
/// fire) + console render" - this file proves the backend half against real Postgres, the identical
/// "handler against real repositories, real applier, no HTTP host" shape
/// <see cref="ChannelAddOnPurchaseEndToEndTests"/> already establishes for the sibling purchase flow.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RenewNowHandlerIntegrationTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HandleAsync_ChargesTheStoredMethod_AndExtendsTheRealCurrentPeriodEndByOnePeriod_InPostgres()
    {
        await SeedSeatPricesAsync();
        var (siteId, subscriptionId, priorPeriodEnd) = await SeedSucceededBaseSubscriptionAsync(requestedSeats: 5);
        var yooKassa = new RecordingYooKassaClient(new ChargeStoredPaymentMethodResult.Success("pmt_renew_now"));

        var result = await CallHandlerAsync(siteId, subscriptionId, yooKassa);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Message : null);
        Assert.Equal(890m, result.Value.AmountRub);
        Assert.Equal(priorPeriodEnd + BillingSubscription.PeriodLength, result.Value.NewPeriodEnd);
        Assert.Equal(890m, yooKassa.LastChargeRequest!.AmountRub);
        Assert.Equal($"renewal:{subscriptionId.Value}:{Now:yyyy-MM-dd}", yooKassa.LastChargeRequest.IdempotenceKey);

        await using var verify = fixture.CreateDbContext();
        var persisted = await verify.BillingSubscriptions.SingleAsync(s => s.Id == subscriptionId);
        Assert.Equal(priorPeriodEnd + BillingSubscription.PeriodLength, persisted.CurrentPeriodEnd);
        Assert.Equal(BillingSubscriptionStatus.Succeeded, persisted.Status);
    }

    /// <summary>The idempotency half of the Done-when - a second `HandleAsync` call the same day, against
    /// the real row this handler's own first call already updated, must refuse rather than charge and
    /// extend a second time.</summary>
    [Fact]
    public async Task HandleAsync_WhenCalledTwiceTheSameDay_TheSecondCallRefuses_AndNeverExtendsThePeriodTwice()
    {
        await SeedSeatPricesAsync();
        var (siteId, subscriptionId, priorPeriodEnd) = await SeedSucceededBaseSubscriptionAsync(requestedSeats: 3);
        var yooKassa = new RecordingYooKassaClient(new ChargeStoredPaymentMethodResult.Success("pmt_renew_now"));

        var first = await CallHandlerAsync(siteId, subscriptionId, yooKassa);
        Assert.True(first.IsSuccess, first.IsFailure ? first.Error!.Value.Message : null);

        var second = await CallHandlerAsync(siteId, subscriptionId, yooKassa);

        Assert.True(second.IsFailure);
        Assert.Equal("Billing.SubscriptionAlreadyRenewedToday", second.Error!.Value.Code);

        await using var verify = fixture.CreateDbContext();
        var persisted = await verify.BillingSubscriptions.SingleAsync(s => s.Id == subscriptionId);
        // Exactly one period's worth of extension, not two, for the two calls made.
        Assert.Equal(priorPeriodEnd + BillingSubscription.PeriodLength, persisted.CurrentPeriodEnd);
    }

    [Fact]
    public async Task HandleAsync_WhenYooKassaRefuses_LeavesTheRealRowSucceeded_NotPastDue()
    {
        await SeedSeatPricesAsync();
        var (siteId, subscriptionId, priorPeriodEnd) = await SeedSucceededBaseSubscriptionAsync(requestedSeats: 3);
        var yooKassa = new RecordingYooKassaClient(new ChargeStoredPaymentMethodResult.Refused("card declined"));

        var result = await CallHandlerAsync(siteId, subscriptionId, yooKassa);

        Assert.True(result.IsFailure);
        Assert.Equal("Billing.PaymentProviderRefused", result.Error!.Value.Code);

        await using var verify = fixture.CreateDbContext();
        var persisted = await verify.BillingSubscriptions.SingleAsync(s => s.Id == subscriptionId);
        Assert.Equal(BillingSubscriptionStatus.Succeeded, persisted.Status);
        Assert.Equal(priorPeriodEnd, persisted.CurrentPeriodEnd);
    }

    private async Task<Result<RenewNowResult>> CallHandlerAsync(SiteId siteId, BillingSubscriptionId subscriptionId, IYooKassaPaymentsClient yooKassa)
    {
        await using var db = fixture.CreateDbContext();
        var operatorId = new OperatorId(Guid.NewGuid());
        var handler = new RenewNowHandler(
            new BillingSubscriptionRepository(db), new AllowAllPermissionChecker(), yooKassa, new PriceCatalogRepository(db),
            BuildRenewalApplier(db), new FixedClock(Now));

        return await handler.HandleAsync(new RenewNow(operatorId, siteId, subscriptionId), CancellationToken.None);
    }

    private static ISubscriptionRenewalApplier BuildRenewalApplier(AgoChatDbContext db)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var entitlements = new ConfiguredBillingOptionEntitlementProvider(config);
        var grants = new ModuleQuantityGrantStore(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator(), new FixedClock(Now));
        var roleSeatReconciler = new OperatorRoleSeatReconciler(new OperatorRoleRepository(db));
        return new SubscriptionRenewalApplier(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator(), grants, entitlements, roleSeatReconciler);
    }

    private async Task<(SiteId SiteId, BillingSubscriptionId SubscriptionId, DateTimeOffset PeriodEnd)> SeedSucceededBaseSubscriptionAsync(int requestedSeats)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var subscriptionId = new BillingSubscriptionId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", [], tier: SubscriptionTierBands.Starter, seatLimit: requestedSeats));
        var subscription = BillingSubscription.Create(
            subscriptionId, siteId, $"pmt_{subscriptionId.Value:N}", requestedSeats, SubscriptionTierBands.Starter,
            baseSeatPriceVersion: 1, extraSeatPriceVersion: 1, createdAt: Now - TimeSpan.FromDays(20));
        subscription.MarkSucceeded("card_on_file", Now - TimeSpan.FromDays(20));
        db.BillingSubscriptions.Add(subscription);
        await db.SaveChangesAsync();
        return (siteId, subscriptionId, subscription.CurrentPeriodEnd!.Value);
    }

    private const decimal SeededBaseSeatPriceRub = 490m;

    private const decimal SeededExtraSeatPriceRub = 200m;

    private async Task SeedSeatPricesAsync()
    {
        await using var db = fixture.CreateDbContext();
        var prices = new PriceCatalogRepository(db);

        var baseResource = await prices.GetByKeyAsync(SubscriptionTierBands.BaseSeatPriceKey, CancellationToken.None)
            ?? PricedResource.Create(new PricedResourceId(Guid.NewGuid()), SubscriptionTierBands.BaseSeatPriceKey);
        baseResource.Publish(new PublishedPriceVersionId(Guid.NewGuid()), SeededBaseSeatPriceRub, Now);
        await prices.SaveAsync(baseResource, CancellationToken.None);

        var extraResource = await prices.GetByKeyAsync(SubscriptionTierBands.ExtraSeatPriceKey, CancellationToken.None)
            ?? PricedResource.Create(new PricedResourceId(Guid.NewGuid()), SubscriptionTierBands.ExtraSeatPriceKey);
        extraResource.Publish(new PublishedPriceVersionId(Guid.NewGuid()), SeededExtraSeatPriceRub, Now);
        await prices.SaveAsync(extraResource, CancellationToken.None);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class AllowAllPermissionChecker : IPermissionChecker
    {
        public Task<bool> HasPermissionAsync(OperatorId operatorId, SiteId siteId, Permission permission, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<string>> GetPermissionsAsync(OperatorId operatorId, SiteId siteId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not part of the renew-now path under test.");

        public Task<int> CountNonRemovedHoldersAsync(SiteId siteId, Permission permission, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not part of the renew-now path under test.");

        public Task<IReadOnlyList<OperatorId>> ListNonRemovedHolderIdsAsync(SiteId siteId, Permission permission, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not part of the renew-now path under test.");
    }

    /// <summary>Stands in for ЮKassa's own charge-on-file call - hands back whatever result the test
    /// wants and records the request, the same "a fake at the port, real everything below it" split
    /// every other integration test in this suite uses.</summary>
    private sealed class RecordingYooKassaClient(ChargeStoredPaymentMethodResult result) : IYooKassaPaymentsClient
    {
        public ChargeStoredPaymentMethodRequest? LastChargeRequest { get; private set; }

        public Task<CreatePaymentResult> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Renew-now never creates a fresh payment.");

        public Task<CreatePaymentWithTokenResult> CreatePaymentWithTokenAsync(
            CreatePaymentWithTokenRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Renew-now never creates a token payment.");

        public Task<ChargeStoredPaymentMethodResult> ChargeStoredPaymentMethodAsync(
            ChargeStoredPaymentMethodRequest request, CancellationToken cancellationToken)
        {
            LastChargeRequest = request;
            return Task.FromResult(result);
        }

        public Task<GetPaymentResult> GetPaymentAsync(string paymentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Renew-now never re-queries a payment.");
    }
}
