using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `14-12`/`adr/0079` decision 4: proves the storage-level backstop
/// <c>ChannelIdentityConfiguration</c>'s own remarks describe - a partial unique index on
/// <c>(site_id, kind, external_address)</c> filtered to <c>active</c>, not the plain one `14-01` shipped,
/// specifically so an unlinked identity never blocks a fresh link of the same external address later.
/// This is the one guarantee no fake can stand in for (`testing.md`: "never mock the database for a
/// guarantee the schema itself provides") - a plain unique index would make the second insert below fail
/// with a real Postgres constraint violation.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ChannelIdentityRepositoryTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private async Task<(SiteId Site, VisitorId FirstVisitor, VisitorId SecondVisitor)> SeedSiteAndTwoVisitorsAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var firstVisitor = new VisitorId(Guid.NewGuid());
        var secondVisitor = new VisitorId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(firstVisitor, siteId, Now));
        db.Visitors.Add(new Visitor(secondVisitor, siteId, Now));
        await db.SaveChangesAsync();

        return (siteId, firstVisitor, secondVisitor);
    }

    [Fact]
    public async Task SaveAsync_ThenFindAsync_RoundTripsAnActiveIdentity()
    {
        var (siteId, visitorId, _) = await SeedSiteAndTwoVisitorsAsync();
        var address = new ExternalChannelAddress($"addr-{Guid.NewGuid():N}");
        var identity = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), siteId, ChannelKind.Telegram, address, visitorId, Now);

        await using (var db = fixture.CreateDbContext())
        {
            await new ChannelIdentityRepository(db).SaveAsync(identity, CancellationToken.None);
        }

        await using var readDb = fixture.CreateDbContext();
        var loaded = await new ChannelIdentityRepository(readDb).FindAsync(
            siteId, ChannelKind.Telegram, address, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(visitorId, loaded.VisitorId);
        Assert.True(loaded.Active);
    }

    /// <summary>The headline guarantee: unlink, then link the identical (site, kind, address) to a
    /// different visitor - a plain unique index would reject the second insert outright.</summary>
    [Fact]
    public async Task AfterUnlink_TheSameAddressCanBeRelinkedToADifferentVisitor()
    {
        var (siteId, firstVisitor, secondVisitor) = await SeedSiteAndTwoVisitorsAsync();
        var address = new ExternalChannelAddress($"addr-{Guid.NewGuid():N}");
        var original = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), siteId, ChannelKind.Telegram, address, firstVisitor, Now);

        await using (var db = fixture.CreateDbContext())
        {
            await new ChannelIdentityRepository(db).SaveAsync(original, CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new ChannelIdentityRepository(db);
            var loaded = await repository.FindAsync(siteId, ChannelKind.Telegram, address, CancellationToken.None);
            loaded!.Unlink(Now.AddHours(1));
            await repository.SaveAsync(loaded, CancellationToken.None);
        }

        // The re-link: a brand-new row for the identical (site, kind, address) key, now pointing at a
        // different visitor. This SaveAsync is the real assertion - it must not throw a unique-
        // constraint violation.
        var relinked = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), siteId, ChannelKind.Telegram, address, secondVisitor, Now.AddHours(2));
        await using (var db = fixture.CreateDbContext())
        {
            await new ChannelIdentityRepository(db).SaveAsync(relinked, CancellationToken.None);
        }

        await using var readDb = fixture.CreateDbContext();
        var current = await new ChannelIdentityRepository(readDb).FindAsync(
            siteId, ChannelKind.Telegram, address, CancellationToken.None);

        Assert.NotNull(current);
        Assert.Equal(secondVisitor, current.VisitorId);
    }

    [Fact]
    public async Task FindAsync_WhenTheOnlyIdentityForThatAddressIsUnlinked_ReturnsNull()
    {
        var (siteId, visitorId, _) = await SeedSiteAndTwoVisitorsAsync();
        var address = new ExternalChannelAddress($"addr-{Guid.NewGuid():N}");
        var identity = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), siteId, ChannelKind.Telegram, address, visitorId, Now);
        identity.Unlink(Now.AddHours(1));

        await using (var db = fixture.CreateDbContext())
        {
            await new ChannelIdentityRepository(db).SaveAsync(identity, CancellationToken.None);
        }

        await using var readDb = fixture.CreateDbContext();
        var found = await new ChannelIdentityRepository(readDb).FindAsync(
            siteId, ChannelKind.Telegram, address, CancellationToken.None);

        Assert.Null(found);
    }

    [Fact]
    public async Task FindMostRecentForVisitorAsync_ExcludesAnUnlinkedIdentity()
    {
        var (siteId, visitorId, _) = await SeedSiteAndTwoVisitorsAsync();
        var identity = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), siteId, ChannelKind.Telegram,
            new ExternalChannelAddress($"addr-{Guid.NewGuid():N}"), visitorId, Now);
        identity.Unlink(Now.AddHours(1));

        await using (var db = fixture.CreateDbContext())
        {
            await new ChannelIdentityRepository(db).SaveAsync(identity, CancellationToken.None);
        }

        await using var readDb = fixture.CreateDbContext();
        var found = await new ChannelIdentityRepository(readDb).FindMostRecentForVisitorAsync(
            visitorId, CancellationToken.None);

        Assert.Null(found);
    }

    [Fact]
    public async Task ListActiveForVisitorAsync_ExcludesAnUnlinkedIdentity_AndReturnsTheActiveOnes()
    {
        var (siteId, visitorId, _) = await SeedSiteAndTwoVisitorsAsync();
        var active = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), siteId, ChannelKind.Telegram,
            new ExternalChannelAddress($"addr-{Guid.NewGuid():N}"), visitorId, Now);
        var unlinked = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), siteId, ChannelKind.Vk,
            new ExternalChannelAddress($"addr-{Guid.NewGuid():N}"), visitorId, Now);
        unlinked.Unlink(Now.AddHours(1));

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new ChannelIdentityRepository(db);
            await repository.SaveAsync(active, CancellationToken.None);
            await repository.SaveAsync(unlinked, CancellationToken.None);
        }

        await using var readDb = fixture.CreateDbContext();
        var found = await new ChannelIdentityRepository(readDb).ListActiveForVisitorAsync(visitorId, CancellationToken.None);

        var summary = Assert.Single(found);
        Assert.Equal(active.Id, summary.Id);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsTheIdentityRegardlessOfActiveState()
    {
        var (siteId, visitorId, _) = await SeedSiteAndTwoVisitorsAsync();
        var identity = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), siteId, ChannelKind.Telegram,
            new ExternalChannelAddress($"addr-{Guid.NewGuid():N}"), visitorId, Now);
        identity.Unlink(Now.AddHours(1));

        await using (var db = fixture.CreateDbContext())
        {
            await new ChannelIdentityRepository(db).SaveAsync(identity, CancellationToken.None);
        }

        await using var readDb = fixture.CreateDbContext();
        var found = await new ChannelIdentityRepository(readDb).GetByIdAsync(identity.Id, CancellationToken.None);

        Assert.NotNull(found);
        Assert.False(found.Active);
    }
}
