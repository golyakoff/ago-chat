using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Contracts;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `13-02`: <see cref="BillingWebhookApplier"/>'s own one-transaction shape, proven against a real
/// Postgres (`PostgresFixture`, the same "no mocked database" bar every other repository test in this
/// suite holds itself to) - idempotency ledger, terminal-state transition, and (on
/// `payment.succeeded`) `Site.Tier`/`Site.SeatLimit`, committed together or not at all. This is the
/// layer beneath <see cref="YooKassaWebhookEndpointTests"/>: that file proves the HTTP surface (auth,
/// status codes); this file proves the transaction itself, including the case
/// (<c>SubscriptionNotFound</c>) that never reaches the endpoint's own "everything short of a bad
/// signature is 200" branch in a way worth a dedicated HTTP-level test.
/// </summary>
[Collection(PostgresCollection.Name)]
public class BillingWebhookApplierTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ApplyAsync_WhenPaymentSucceeded_UpdatesSiteTierAndSeatLimit_MarksTheSubscriptionSucceeded_AndWritesOneOutboxRow()
    {
        var (siteId, paymentId) = await SeedPendingSubscriptionAsync(requestedSeats: 5, tier: SubscriptionTierBands.Starter);

        await using (var db = fixture.CreateDbContext())
        {
            var applier = BuildApplier(db);
            var result = await applier.ApplyAsync(
                new BillingWebhookApplyRequest(paymentId, "payment.succeeded", "card_abc123", Now), CancellationToken.None);

            var applied = Assert.IsType<BillingWebhookApplyResult.Applied>(result);
            Assert.Equal(siteId, applied.SiteId);
            Assert.Equal(SubscriptionTierBands.Starter, applied.Tier);
            Assert.Equal(5, applied.SeatLimit);
        }

        await using var verify = fixture.CreateDbContext();
        var site = await verify.Sites.SingleAsync(s => s.Id == siteId, CancellationToken.None);
        Assert.Equal(SubscriptionTierBands.Starter, site.Tier);
        Assert.Equal(5, site.SeatLimit);

        var subscription = await verify.BillingSubscriptions.SingleAsync(s => s.YooKassaPaymentId == paymentId, CancellationToken.None);
        Assert.Equal(BillingSubscriptionStatus.Succeeded, subscription.Status);
        Assert.Equal("card_abc123", subscription.PaymentMethodId);

        var outboxRows = await verify.Set<OutboxMessage>()
            .Where(o => o.PartitionKey == siteId.Value.ToString())
            .ToListAsync(CancellationToken.None);
        var row = Assert.Single(outboxRows);
        Assert.Equal(nameof(SiteSettingsChanged), row.Type);
        Assert.Null(row.PublishedAt);

        var ledgerRow = await verify.BillingWebhookEvents.SingleAsync(
            e => e.YooKassaPaymentId == paymentId && e.EventType == "payment.succeeded", CancellationToken.None);
        Assert.Equal(Now, ledgerRow.ReceivedAt);
    }

    [Fact]
    public async Task ApplyAsync_WhenTheSamePaymentSucceededEventArrivesTwice_DoesNotDoubleApply()
    {
        var (siteId, paymentId) = await SeedPendingSubscriptionAsync(requestedSeats: 10, tier: SubscriptionTierBands.Growth);

        await using (var db = fixture.CreateDbContext())
        {
            var applier = BuildApplier(db);
            var first = await applier.ApplyAsync(
                new BillingWebhookApplyRequest(paymentId, "payment.succeeded", "card_abc", Now), CancellationToken.None);
            Assert.IsType<BillingWebhookApplyResult.Applied>(first);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var applier = BuildApplier(db);
            var second = await applier.ApplyAsync(
                new BillingWebhookApplyRequest(paymentId, "payment.succeeded", "card_abc", Now), CancellationToken.None);
            Assert.IsType<BillingWebhookApplyResult.Duplicate>(second);
        }

        await using var verify = fixture.CreateDbContext();
        var site = await verify.Sites.SingleAsync(s => s.Id == siteId, CancellationToken.None);
        Assert.Equal(SubscriptionTierBands.Growth, site.Tier);
        Assert.Equal(10, site.SeatLimit);

        // Exactly one outbox row - a second identical apply must not enqueue a second invalidation.
        var outboxRows = await verify.Set<OutboxMessage>()
            .Where(o => o.PartitionKey == siteId.Value.ToString())
            .ToListAsync(CancellationToken.None);
        Assert.Single(outboxRows);
    }

    [Fact]
    public async Task ApplyAsync_WhenPaymentCanceled_MarksTheSubscriptionFailed_AndLeavesTheSiteOnFreeTier()
    {
        var (siteId, paymentId) = await SeedPendingSubscriptionAsync(requestedSeats: 5, tier: SubscriptionTierBands.Starter);

        await using (var db = fixture.CreateDbContext())
        {
            var applier = BuildApplier(db);
            var result = await applier.ApplyAsync(
                new BillingWebhookApplyRequest(paymentId, "payment.canceled", null, Now), CancellationToken.None);

            Assert.IsType<BillingWebhookApplyResult.Canceled>(result);
        }

        await using var verify = fixture.CreateDbContext();
        var site = await verify.Sites.SingleAsync(s => s.Id == siteId, CancellationToken.None);
        Assert.Equal("free", site.Tier);
        Assert.Equal(1, site.SeatLimit);

        var subscription = await verify.BillingSubscriptions.SingleAsync(s => s.YooKassaPaymentId == paymentId, CancellationToken.None);
        Assert.Equal(BillingSubscriptionStatus.Failed, subscription.Status);

        Assert.False(await verify.Set<OutboxMessage>().AnyAsync(o => o.PartitionKey == siteId.Value.ToString(), CancellationToken.None));
    }

    [Fact]
    public async Task ApplyAsync_WhenNoSubscriptionMatchesThePaymentId_StillRecordsTheLedgerRow()
    {
        await using (var db = fixture.CreateDbContext())
        {
            var applier = BuildApplier(db);
            var result = await applier.ApplyAsync(
                new BillingWebhookApplyRequest("pmt_never_created", "payment.succeeded", "card_x", Now), CancellationToken.None);

            Assert.IsType<BillingWebhookApplyResult.SubscriptionNotFound>(result);
        }

        await using var verify = fixture.CreateDbContext();
        Assert.True(await verify.BillingWebhookEvents.AnyAsync(
            e => e.YooKassaPaymentId == "pmt_never_created" && e.EventType == "payment.succeeded", CancellationToken.None));
    }

    [Fact]
    public async Task ApplyAsync_WhenTheEventTypeIsUnrecognised_RecordsTheLedgerRow_AndLeavesTheSiteUntouched()
    {
        var (siteId, paymentId) = await SeedPendingSubscriptionAsync(requestedSeats: 5, tier: SubscriptionTierBands.Starter);

        await using (var db = fixture.CreateDbContext())
        {
            var applier = BuildApplier(db);
            var result = await applier.ApplyAsync(
                new BillingWebhookApplyRequest(paymentId, "payment.waiting_for_capture", null, Now), CancellationToken.None);

            Assert.IsType<BillingWebhookApplyResult.Ignored>(result);
        }

        await using var verify = fixture.CreateDbContext();
        var site = await verify.Sites.SingleAsync(s => s.Id == siteId, CancellationToken.None);
        Assert.Equal("free", site.Tier);
    }

    private static BillingWebhookApplier BuildApplier(AgoChatDbContext db) =>
        new(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator());

    private async Task<(SiteId SiteId, string PaymentId)> SeedPendingSubscriptionAsync(int requestedSeats, string tier)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var paymentId = $"pmt_{Guid.NewGuid():N}";

        await using var seed = fixture.CreateDbContext();
        seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        seed.BillingSubscriptions.Add(BillingSubscription.Create(
            new BillingSubscriptionId(Guid.NewGuid()), siteId, paymentId, requestedSeats, tier, Now));
        await seed.SaveChangesAsync(CancellationToken.None);

        return (siteId, paymentId);
    }
}
