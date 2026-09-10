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
/// `25-41`: the identical bar <see cref="SeatChangeApplierTests"/> already sets for the analogous seat
/// purchase - real Postgres, real one-transaction commit across <see cref="BillingSubscription"/> and
/// <see cref="Site"/>, real outbox row. Proves what <see cref="Application.Tests.UseCases.PurchaseAdministratorSlot.PurchaseAdministratorSlotHandlerTests"/>
/// (fakes) cannot: that an Administrator-slot purchase actually lands on the real <c>sites</c> row's
/// own <see cref="Site.AdminLimit"/>, in the same transaction as the subscription row.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AdministratorSlotChangeApplierTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ApplyImmediateIncreaseAsync_RaisesTheSitesAdminLimit_AndStagesTheOutboxRow()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var subscriptionId = new BillingSubscriptionId(Guid.NewGuid());

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", [], tier: SubscriptionTierBands.Starter, seatLimit: 5));
            var seeded = BillingSubscription.Create(
                subscriptionId, siteId, $"pmt_{subscriptionId.Value:N}", 5, SubscriptionTierBands.Starter, 1, 1, Now - BillingSubscription.PeriodLength);
            seeded.MarkSucceeded("card_on_file", Now - BillingSubscription.PeriodLength);
            db.BillingSubscriptions.Add(seeded);
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            IAdministratorSlotChangeApplier applier = new AdministratorSlotChangeApplier(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator());
            await applier.ApplyImmediateIncreaseAsync(
                new AdministratorSlotChangeApplyRequest(subscriptionId, siteId, NewExtraAdministratorCount: 1, AdminExtraPriceVersion: 1, Now),
                CancellationToken.None);
        }

        await using var verify = fixture.CreateDbContext();
        var subscription = await verify.BillingSubscriptions.SingleAsync(s => s.Id == subscriptionId);
        Assert.Equal(1, subscription.ExtraAdministratorsPurchased);
        Assert.Equal(1, subscription.AdminExtraPriceVersion);
        // Untouched - a purchase of an Administrator slot is not a seat purchase.
        Assert.Equal(5, subscription.RequestedSeats);

        var site = await verify.Sites.SingleAsync(s => s.Id == siteId);
        Assert.Equal(SubscriptionTierBands.BusinessAdminsIncluded + 1, site.AdminLimit);
        // Untouched - Tier/SeatLimit are not what this purchase changes.
        Assert.Equal(SubscriptionTierBands.Starter, site.Tier);
        Assert.Equal(5, site.SeatLimit);

        var outboxRow = await verify.Set<OutboxMessage>()
            .Where(o => o.Type == nameof(SiteSettingsChanged))
            .OrderByDescending(o => o.OccurredAt)
            .FirstOrDefaultAsync();
        Assert.NotNull(outboxRow);
    }
}
