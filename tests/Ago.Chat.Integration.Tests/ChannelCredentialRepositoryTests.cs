using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Integration.Tests;

/// <summary>`14-02`/`adr/0069`: proves the EF mapping round-trips a <see cref="ChannelCredential"/> -
/// <c>WebhookEndpointRepositoryTests</c>' own shape - and, separately, proves the
/// storage-level backstop <c>ChannelCredentialConfiguration</c>'s own remarks describe: a partial
/// unique index on <c>(site_id, kind)</c> filtered to <c>active</c>, not a plain one, specifically so a
/// revoked credential never blocks its own replacement.</summary>
[Collection(PostgresCollection.Name)]
public class ChannelCredentialRepositoryTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private async Task SeedSite(SiteId siteId)
    {
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task SaveAsync_ThenGetByIdAsync_RoundTripsTheCredential()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await SeedSite(siteId);
        var credential = ChannelCredential.Register(
            new ChannelCredentialId(Guid.NewGuid()), siteId, ChannelKind.Max, [1, 2, 3], [4, 5, 6], Now);

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new ChannelCredentialRepository(db);
            await repository.SaveAsync(credential, CancellationToken.None);
        }

        await using var readDb = fixture.CreateDbContext();
        var readRepository = new ChannelCredentialRepository(readDb);
        var loaded = await readRepository.GetByIdAsync(credential.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(ChannelKind.Max, loaded.Kind);
        Assert.True(loaded.Active);
        Assert.Equal(new byte[] { 1, 2, 3 }, loaded.TokenCiphertext);
        Assert.Equal(new byte[] { 4, 5, 6 }, loaded.WebhookSecretHash);
    }

    [Fact]
    public async Task GetActiveAsync_OnlyReturnsAnActiveCredentialForThatSiteAndChannel()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var otherSiteId = new SiteId(Guid.NewGuid());
        await SeedSite(siteId);
        await SeedSite(otherSiteId);

        var mine = ChannelCredential.Register(new ChannelCredentialId(Guid.NewGuid()), siteId, ChannelKind.Max, [1], [1], Now);
        var theirs = ChannelCredential.Register(new ChannelCredentialId(Guid.NewGuid()), otherSiteId, ChannelKind.Max, [2], [2], Now);

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new ChannelCredentialRepository(db);
            await repository.SaveAsync(mine, CancellationToken.None);
            await repository.SaveAsync(theirs, CancellationToken.None);
        }

        await using var readDb = fixture.CreateDbContext();
        var readRepository = new ChannelCredentialRepository(readDb);
        var active = await readRepository.GetActiveAsync(siteId, ChannelKind.Max, CancellationToken.None);

        Assert.NotNull(active);
        Assert.Equal(mine.Id, active.Id);
    }

    [Fact]
    public async Task Revoke_ThenSaveAsync_PersistsActiveAsFalse()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await SeedSite(siteId);
        var credential = ChannelCredential.Register(new ChannelCredentialId(Guid.NewGuid()), siteId, ChannelKind.Max, [1], [1], Now);

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new ChannelCredentialRepository(db);
            await repository.SaveAsync(credential, CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new ChannelCredentialRepository(db);
            var loaded = await repository.GetByIdAsync(credential.Id, CancellationToken.None);
            loaded!.Revoke();
            await repository.SaveAsync(loaded, CancellationToken.None);
        }

        await using var readDb = fixture.CreateDbContext();
        var readRepository = new ChannelCredentialRepository(readDb);
        var reloaded = await readRepository.GetByIdAsync(credential.Id, CancellationToken.None);

        Assert.False(reloaded!.Active);
        // GetActiveAsync must no longer find it - RegisterChannelCredentialHandler's own
        // "no active credential" check relies on exactly this.
        Assert.Null(await readRepository.GetActiveAsync(siteId, ChannelKind.Max, CancellationToken.None));
    }

    /// <summary>The storage-level backstop itself: two <em>active</em> rows for the same (site, kind)
    /// must be refused by the database, independent of whatever check the Application handler makes -
    /// `adr/0019`'s "the index is the backstop, not the primary mechanism" division.</summary>
    [Fact]
    public async Task TwoActiveCredentialsForTheSameSiteAndChannel_ViolatesTheUniqueIndex()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await SeedSite(siteId);
        var first = ChannelCredential.Register(new ChannelCredentialId(Guid.NewGuid()), siteId, ChannelKind.Max, [1], [1], Now);
        var second = ChannelCredential.Register(new ChannelCredentialId(Guid.NewGuid()), siteId, ChannelKind.Max, [2], [2], Now);

        await using var db = fixture.CreateDbContext();
        var repository = new ChannelCredentialRepository(db);
        await repository.SaveAsync(first, CancellationToken.None);

        await Assert.ThrowsAsync<DbUpdateException>(() => repository.SaveAsync(second, CancellationToken.None));
    }

    /// <summary>`14-08`: the one column VK needs and MAX/Telegram never populate -
    /// <c>ChannelCredential.ProviderAccountId</c>'s own remarks - round-tripped through a real Postgres
    /// column, nullable, unlike every other column on this table.</summary>
    [Fact]
    public async Task SaveAsync_WithAProviderAccountId_RoundTripsIt()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await SeedSite(siteId);
        // `14-10`'s own ux_channel_credentials_kind_provideraccountid_active index made ProviderAccountId
        // unique per (kind, active) across the whole table, not just this test's own row - a fresh value
        // per run, not a shared literal, the same fix this item's own report names for VkWebhookEndpointsTests.
        var providerAccountId = $"555{Guid.NewGuid():N}"[..10];
        var credential = ChannelCredential.Register(
            new ChannelCredentialId(Guid.NewGuid()), siteId, ChannelKind.Vk, [1], [1], Now, providerAccountId: providerAccountId);

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new ChannelCredentialRepository(db);
            await repository.SaveAsync(credential, CancellationToken.None);
        }

        await using var readDb = fixture.CreateDbContext();
        var readRepository = new ChannelCredentialRepository(readDb);
        var loaded = await readRepository.GetByIdAsync(credential.Id, CancellationToken.None);

        Assert.Equal(providerAccountId, loaded!.ProviderAccountId);
    }

    /// <summary>The partial index's whole point: revoking the first credential must let a second,
    /// active one for the identical (site, kind) pair be saved without violating anything -
    /// `ChannelCredentialConfiguration`'s own remarks on why the index is filtered to `active`.</summary>
    [Fact]
    public async Task AfterRevokingTheFirst_ASecondActiveCredentialForTheSameSiteAndChannel_CanBeSaved()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await SeedSite(siteId);
        var first = ChannelCredential.Register(new ChannelCredentialId(Guid.NewGuid()), siteId, ChannelKind.Max, [1], [1], Now);

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new ChannelCredentialRepository(db);
            await repository.SaveAsync(first, CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new ChannelCredentialRepository(db);
            var loaded = await repository.GetByIdAsync(first.Id, CancellationToken.None);
            loaded!.Revoke();
            await repository.SaveAsync(loaded, CancellationToken.None);
        }

        var second = ChannelCredential.Register(new ChannelCredentialId(Guid.NewGuid()), siteId, ChannelKind.Max, [2], [2], Now);
        await using var finalDb = fixture.CreateDbContext();
        var finalRepository = new ChannelCredentialRepository(finalDb);
        await finalRepository.SaveAsync(second, CancellationToken.None); // must not throw

        var active = await finalRepository.GetActiveAsync(siteId, ChannelKind.Max, CancellationToken.None);
        Assert.Equal(second.Id, active!.Id);
    }

    /// <summary>`14-10`: the lookup <c>WhatsAppWebhookEndpoints</c> resolves an inbound delivery's tenant
    /// by - <c>IChannelCredentialRepository.GetActiveByProviderAccountIdAsync</c>'s own remarks on why
    /// WhatsApp needs this where no other channel does (its webhook carries no per-tenant path segment).
    /// Two sites both hold WhatsApp credentials here, with different `provider_account_id`s, to prove the
    /// lookup does not simply return the first WhatsApp row it finds.</summary>
    [Fact]
    public async Task GetActiveByProviderAccountIdAsync_ReturnsTheCredentialForThatProviderAccountId()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var otherSiteId = new SiteId(Guid.NewGuid());
        await SeedSite(siteId);
        await SeedSite(otherSiteId);

        var mine = ChannelCredential.Register(
            new ChannelCredentialId(Guid.NewGuid()), siteId, ChannelKind.WhatsApp, [1], [1], Now, providerAccountId: "111111111");
        var theirs = ChannelCredential.Register(
            new ChannelCredentialId(Guid.NewGuid()), otherSiteId, ChannelKind.WhatsApp, [2], [2], Now, providerAccountId: "222222222");

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new ChannelCredentialRepository(db);
            await repository.SaveAsync(mine, CancellationToken.None);
            await repository.SaveAsync(theirs, CancellationToken.None);
        }

        await using var readDb = fixture.CreateDbContext();
        var readRepository = new ChannelCredentialRepository(readDb);
        var found = await readRepository.GetActiveByProviderAccountIdAsync(ChannelKind.WhatsApp, "111111111", CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(mine.Id, found.Id);
    }

    [Fact]
    public async Task GetActiveByProviderAccountIdAsync_ForAnUnregisteredId_ReturnsNull()
    {
        await using var db = fixture.CreateDbContext();
        var repository = new ChannelCredentialRepository(db);

        var found = await repository.GetActiveByProviderAccountIdAsync(
            ChannelKind.WhatsApp, $"never-registered-{Guid.NewGuid():N}", CancellationToken.None);

        Assert.Null(found);
    }

    /// <summary>`14-10`'s own storage-level backstop, the identical "index is the backstop, not the
    /// primary mechanism" division <see cref="TwoActiveCredentialsForTheSameSiteAndChannel_ViolatesTheUniqueIndex"/>
    /// already proves for the `(site_id, kind)` index - here for `(kind, provider_account_id)`: two
    /// *different* sites registering the identical `phone_number_id` must be refused by the database,
    /// since nothing else in this schema would catch a shop's site pasting in another shop's number (or
    /// a re-registration bug) before it silently misrouted a visitor's conversation.</summary>
    [Fact]
    public async Task TwoActiveCredentialsWithTheSameProviderAccountId_ViolatesTheNewUniqueIndex()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var otherSiteId = new SiteId(Guid.NewGuid());
        await SeedSite(siteId);
        await SeedSite(otherSiteId);

        var first = ChannelCredential.Register(
            new ChannelCredentialId(Guid.NewGuid()), siteId, ChannelKind.WhatsApp, [1], [1], Now, providerAccountId: "333333333");
        var second = ChannelCredential.Register(
            new ChannelCredentialId(Guid.NewGuid()), otherSiteId, ChannelKind.WhatsApp, [2], [2], Now, providerAccountId: "333333333");

        await using var db = fixture.CreateDbContext();
        var repository = new ChannelCredentialRepository(db);
        await repository.SaveAsync(first, CancellationToken.None);

        await Assert.ThrowsAsync<DbUpdateException>(() => repository.SaveAsync(second, CancellationToken.None));
    }
}
