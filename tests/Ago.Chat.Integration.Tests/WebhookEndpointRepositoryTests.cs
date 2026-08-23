using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;

namespace Ago.Chat.Integration.Tests;

[Collection(PostgresCollection.Name)]
public class WebhookEndpointRepositoryTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private async Task SeedSite(SiteId siteId)
    {
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task SaveAsync_ThenGetByIdAsync_RoundTripsTheEndpoint()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await SeedSite(siteId);
        var endpoint = WebhookEndpoint.Register(
            new WebhookEndpointId(Guid.NewGuid()), siteId, new Uri("https://shop.example.com/hooks/ago"), [1, 2, 3, 4], Now);

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new WebhookEndpointRepository(db);
            await repository.SaveAsync(endpoint, CancellationToken.None);
        }

        await using var readDb = fixture.CreateDbContext();
        var readRepository = new WebhookEndpointRepository(readDb);
        var loaded = await readRepository.GetByIdAsync(endpoint.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(endpoint.Url, loaded.Url);
        Assert.True(loaded.Active);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, loaded.SecretCiphertext);
    }

    [Fact]
    public async Task GetAllForSiteAsync_OnlyReturnsEndpointsForThatSite()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var otherSiteId = new SiteId(Guid.NewGuid());
        await SeedSite(siteId);
        await SeedSite(otherSiteId);

        var mine = WebhookEndpoint.Register(
            new WebhookEndpointId(Guid.NewGuid()), siteId, new Uri("https://shop.example.com/hooks"), [1], Now);
        var theirs = WebhookEndpoint.Register(
            new WebhookEndpointId(Guid.NewGuid()), otherSiteId, new Uri("https://other.example.com/hooks"), [2], Now);

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new WebhookEndpointRepository(db);
            await repository.SaveAsync(mine, CancellationToken.None);
            await repository.SaveAsync(theirs, CancellationToken.None);
        }

        await using var readDb = fixture.CreateDbContext();
        var readRepository = new WebhookEndpointRepository(readDb);
        var forSite = await readRepository.GetAllForSiteAsync(siteId, CancellationToken.None);

        var result = Assert.Single(forSite);
        Assert.Equal(mine.Id, result.Id);
    }

    [Fact]
    public async Task Revoke_ThenSaveAsync_PersistsActiveAsFalse()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await SeedSite(siteId);
        var endpoint = WebhookEndpoint.Register(
            new WebhookEndpointId(Guid.NewGuid()), siteId, new Uri("https://shop.example.com/hooks"), [1], Now);

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new WebhookEndpointRepository(db);
            await repository.SaveAsync(endpoint, CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new WebhookEndpointRepository(db);
            var loaded = await repository.GetByIdAsync(endpoint.Id, CancellationToken.None);
            loaded!.Revoke();
            await repository.SaveAsync(loaded, CancellationToken.None);
        }

        await using var readDb = fixture.CreateDbContext();
        var readRepository = new WebhookEndpointRepository(readDb);
        var reloaded = await readRepository.GetByIdAsync(endpoint.Id, CancellationToken.None);

        Assert.False(reloaded!.Active);
    }
}
