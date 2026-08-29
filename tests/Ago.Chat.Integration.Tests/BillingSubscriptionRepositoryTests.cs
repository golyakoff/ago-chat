using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Integration.Tests;

/// <summary>`13-04`: <see cref="BillingSubscriptionRepository.GetLatestForSiteAsync"/> proven against a
/// real Postgres (`PostgresFixture`, the same "no mocked database" bar every other repository test in
/// this suite holds itself to) - the console billing screen's own read path, and the port's own remarks
/// on why "most recently created" is the right ordering rather than, say, the highest `Status`.</summary>
[Collection(PostgresCollection.Name)]
public class BillingSubscriptionRepositoryTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetLatestForSiteAsync_WhenSiteHasNoSubscriptions_ReturnsNull()
    {
        var siteId = await SeedSiteAsync();

        await using var db = fixture.CreateDbContext();
        var repository = new BillingSubscriptionRepository(db);

        var result = await repository.GetLatestForSiteAsync(siteId, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetLatestForSiteAsync_WhenSiteHasMultipleSubscriptions_ReturnsTheMostRecentlyCreatedOne()
    {
        var siteId = await SeedSiteAsync();

        await using (var seed = fixture.CreateDbContext())
        {
            seed.BillingSubscriptions.Add(BillingSubscription.Create(
                new BillingSubscriptionId(Guid.NewGuid()), siteId, "pmt_old", requestedSeats: 3, tier: SubscriptionTierBands.Starter,
                Now - TimeSpan.FromDays(90)));
            seed.BillingSubscriptions.Add(BillingSubscription.Create(
                new BillingSubscriptionId(Guid.NewGuid()), siteId, "pmt_newest", requestedSeats: 12, tier: SubscriptionTierBands.Growth, Now));
            seed.BillingSubscriptions.Add(BillingSubscription.Create(
                new BillingSubscriptionId(Guid.NewGuid()), siteId, "pmt_middle", requestedSeats: 5, tier: SubscriptionTierBands.Starter,
                Now - TimeSpan.FromDays(30)));
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using var db = fixture.CreateDbContext();
        var repository = new BillingSubscriptionRepository(db);

        var result = await repository.GetLatestForSiteAsync(siteId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("pmt_newest", result!.YooKassaPaymentId);
        Assert.Equal(12, result.RequestedSeats);
    }

    [Fact]
    public async Task GetLatestForSiteAsync_NeverReturnsAnotherSiteSOwnSubscription()
    {
        var siteId = await SeedSiteAsync();
        var otherSiteId = await SeedSiteAsync();

        await using (var seed = fixture.CreateDbContext())
        {
            seed.BillingSubscriptions.Add(BillingSubscription.Create(
                new BillingSubscriptionId(Guid.NewGuid()), otherSiteId, "pmt_other_site", requestedSeats: 20, tier: SubscriptionTierBands.Growth,
                Now));
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using var db = fixture.CreateDbContext();
        var repository = new BillingSubscriptionRepository(db);

        var result = await repository.GetLatestForSiteAsync(siteId, CancellationToken.None);

        Assert.Null(result);
    }

    private async Task<SiteId> SeedSiteAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using var seed = fixture.CreateDbContext();
        seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        await seed.SaveChangesAsync(CancellationToken.None);
        return siteId;
    }
}
