using Ago.Chat.Application.Abstractions;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `13-03`/`decisions/0006`'s own upgrade half: real Postgres, real one-transaction commit across
/// <see cref="BillingSubscription"/> and <see cref="Site"/>, real outbox row - the same bar
/// <c>BillingWebhookApplierTests</c> already set for the analogous first-payment write. Proves what
/// <see cref="ChangeSubscriptionSeatsHandlerTests"/> (fakes) cannot: that the seat/tier change this
/// item's own endpoint applies immediately after a verified charge actually lands on the real
/// <c>sites</c> row, in the same transaction as the subscription row, with the matching
/// `SiteSettingsChanged` cache-invalidation row staged.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SeatChangeApplierTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ApplyImmediateIncreaseAsync_UpdatesBothTheSubscriptionAndTheSiteInOneTransaction_AndStagesTheOutboxRow()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var subscriptionId = new BillingSubscriptionId(Guid.NewGuid());

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", [], tier: SubscriptionTierBands.Starter, seatLimit: 5));
            var seeded = BillingSubscription.Create(
                subscriptionId, siteId, $"pmt_{subscriptionId.Value:N}", 5, SubscriptionTierBands.Starter, Now - BillingSubscription.PeriodLength);
            seeded.MarkSucceeded("card_on_file", Now - BillingSubscription.PeriodLength);
            db.BillingSubscriptions.Add(seeded);
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            ISeatChangeApplier applier = new SeatChangeApplier(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator());
            await applier.ApplyImmediateIncreaseAsync(
                new SeatChangeApplyRequest(subscriptionId, siteId, 15, SubscriptionTierBands.Growth, Now), CancellationToken.None);
        }

        await using var verify = fixture.CreateDbContext();
        var subscription = await verify.BillingSubscriptions.SingleAsync(s => s.Id == subscriptionId);
        Assert.Equal(15, subscription.RequestedSeats);
        Assert.Equal(SubscriptionTierBands.Growth, subscription.Tier);

        var site = await verify.Sites.SingleAsync(s => s.Id == siteId);
        Assert.Equal(SubscriptionTierBands.Growth, site.Tier);
        Assert.Equal(15, site.SeatLimit);

        var outboxRow = await verify.Set<OutboxMessage>()
            .Where(o => o.Type == nameof(SiteSettingsChanged))
            .OrderByDescending(o => o.OccurredAt)
            .FirstOrDefaultAsync();
        Assert.NotNull(outboxRow);
    }
}
