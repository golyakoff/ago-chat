using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.CreateTokenPayment;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `26-291`'s own Done-when: "an integration test posts a (test-mode) token, a `BillingSubscription` row
/// is created `Pending`, and the existing webhook path grants on re-query - no change to
/// `ProcessYooKassaWebhookHandler`." This file proves the first half against real Postgres - the identical
/// "handler against real repositories, no HTTP host" shape <see cref="GetBillingStatusHandlerIntegrationTests"/>
/// already establishes - and the second half by composing the real <see cref="BillingWebhookApplierTests"/>-proven
/// applier against the exact row this handler creates, rather than re-proving the webhook path itself (it
/// is untouched by this item, as the Done-when itself states).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CreateTokenPaymentHandlerIntegrationTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HandleAsync_PersistsAPendingSubscription_WithTheTokenPaymentsRealId_InRealPostgres()
    {
        await SeedSeatPricesAsync();
        var siteId = await SeedSiteAsync();
        var yooKassa = new StubYooKassaTokenClient(new CreatePaymentWithTokenResult.Success("pmt_token_real", "pending", "https://yookassa.example/confirm"));

        var result = await CallHandlerAsync(siteId, requestedSeats: 5, paymentToken: "tok_from_sdk", yooKassa);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Message : null);
        Assert.Equal("pending", result.Value.Status);
        Assert.Equal("https://yookassa.example/confirm", result.Value.ConfirmationUrl);

        await using var verify = fixture.CreateDbContext();
        var persisted = await verify.BillingSubscriptions.SingleAsync(s => s.SiteId == siteId);
        Assert.Equal("pmt_token_real", persisted.YooKassaPaymentId);
        Assert.Equal(5, persisted.RequestedSeats);
        Assert.Equal(SubscriptionTierBands.Starter, persisted.Tier);
        // `13-02`'s own "never the redirect alone" discipline - Pending regardless of ЮKassa's own reply.
        Assert.Equal(BillingSubscriptionStatus.Pending, persisted.Status);
        // The site's own Tier/SeatLimit are untouched - only a verified webhook moves them.
        var site = await verify.Sites.SingleAsync(s => s.Id == siteId);
        Assert.Equal("free", site.Tier);
    }

    /// <summary>The second half of `26-291`'s own Done-when, composed rather than re-proven:
    /// <see cref="BillingWebhookApplier"/> is the real, already-proven applier
    /// (<c>BillingWebhookApplierTests</c>) that grants on a verified webhook - this test only proves it
    /// applies correctly to the specific row this item's own handler creates, never that the webhook path
    /// itself changed (it did not).</summary>
    [Fact]
    public async Task ARowThisHandlerCreates_IsGrantedTheIdenticalWay_AnOrdinaryCheckoutRowAlreadyIs()
    {
        await SeedSeatPricesAsync();
        var siteId = await SeedSiteAsync();
        var yooKassa = new StubYooKassaTokenClient(new CreatePaymentWithTokenResult.Success("pmt_token_grant", "succeeded", ConfirmationUrl: null));

        var result = await CallHandlerAsync(siteId, requestedSeats: 3, paymentToken: "tok_capture", yooKassa);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Message : null);

        BillingSubscriptionId subscriptionId;
        await using (var read = fixture.CreateDbContext())
        {
            subscriptionId = (await read.BillingSubscriptions.SingleAsync(s => s.SiteId == siteId)).Id;
        }

        string yooKassaPaymentId;
        await using (var read = fixture.CreateDbContext())
        {
            yooKassaPaymentId = (await read.BillingSubscriptions.SingleAsync(s => s.Id == subscriptionId)).YooKassaPaymentId;
        }

        await using (var db = fixture.CreateDbContext())
        {
            var applier = new BillingWebhookApplier(db, new Ago.Platform.Persistence.Postgres.EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator());
            var applied = await applier.ApplyAsync(
                new BillingWebhookApplyRequest(yooKassaPaymentId, "payment.succeeded", "card_from_sdk_flow", Now), CancellationToken.None);
            Assert.IsType<BillingWebhookApplyResult.Applied>(applied);
        }

        await using var verify = fixture.CreateDbContext();
        var site = await verify.Sites.SingleAsync(s => s.Id == siteId);
        Assert.Equal(SubscriptionTierBands.Starter, site.Tier);
        Assert.Equal(3, site.SeatLimit);
        var subscription = await verify.BillingSubscriptions.SingleAsync(s => s.Id == subscriptionId);
        Assert.Equal(BillingSubscriptionStatus.Succeeded, subscription.Status);
        Assert.Equal("card_from_sdk_flow", subscription.PaymentMethodId);
    }

    private async Task<Result<CreateTokenPaymentResult>> CallHandlerAsync(
        SiteId siteId, int requestedSeats, string paymentToken, IYooKassaPaymentsClient yooKassa)
    {
        await using var db = fixture.CreateDbContext();
        var operatorId = new OperatorId(Guid.NewGuid());
        var handler = new CreateTokenPaymentHandler(
            new SiteRepository(db), new AllowAllPermissionChecker(), new BillingSubscriptionRepository(db), yooKassa,
            new PriceCatalogRepository(db), new UuidV7Generator(), new FixedClock(Now));

        return await handler.HandleAsync(
            new CreateTokenPayment(operatorId, siteId, requestedSeats, paymentToken), CancellationToken.None);
    }

    private async Task<SiteId> SeedSiteAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        await db.SaveChangesAsync();
        return siteId;
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
            throw new NotSupportedException("Not part of the token-payment path under test.");

        public Task<int> CountNonRemovedHoldersAsync(SiteId siteId, Permission permission, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not part of the token-payment path under test.");

        public Task<IReadOnlyList<OperatorId>> ListNonRemovedHolderIdsAsync(SiteId siteId, Permission permission, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not part of the token-payment path under test.");
    }

    /// <summary>Stands in for ЮKassa's own token-redemption call - hands back whatever result the test
    /// wants, the same "a fake at the port, real everything below it" split every other integration test
    /// in this suite uses.</summary>
    private sealed class StubYooKassaTokenClient(CreatePaymentWithTokenResult result) : IYooKassaPaymentsClient
    {
        public Task<CreatePaymentResult> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The token-payment path never calls the redirect-flow port method.");

        public Task<CreatePaymentWithTokenResult> CreatePaymentWithTokenAsync(
            CreatePaymentWithTokenRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(result);

        public Task<ChargeStoredPaymentMethodResult> ChargeStoredPaymentMethodAsync(
            ChargeStoredPaymentMethodRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The token-payment path never charges a stored method.");

        public Task<GetPaymentResult> GetPaymentAsync(string paymentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The webhook re-query is not exercised by this handler-level test.");
    }
}
