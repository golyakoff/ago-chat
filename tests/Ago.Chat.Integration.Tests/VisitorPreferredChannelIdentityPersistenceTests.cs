using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `14-13`/`adr/0079` decision 5: `Stage14AddVisitorPreferredChannelIdentity` against a real Postgres,
/// applied from scratch by <see cref="PostgresFixture"/>'s own migration run - the same bar
/// `ChannelIdentityPersistenceTests` was held to for `14-12`'s own column, rather than an assertion
/// that the C# compiles. The claim worth proving here is specifically the storage-level one: the
/// nullable FK/value-converter round-trips both a real id and <see langword="null"/>, through the real
/// <see cref="VisitorRepository"/> - something no Application-level fake can prove.
/// </summary>
[Collection(PostgresCollection.Name)]
public class VisitorPreferredChannelIdentityPersistenceTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(
        DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    private async Task<(VisitorId Visitor, ChannelIdentityId Identity)> SeedVisitorAndActiveIdentityAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var identity = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), siteId, ChannelKind.Telegram,
            new ExternalChannelAddress($"tg-{Guid.NewGuid():N}"), visitorId, Now);

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(visitorId, siteId, Now));
        db.ChannelIdentities.Add(identity);
        await db.SaveChangesAsync();

        return (visitorId, identity.Id);
    }

    [Fact]
    public async Task APreferredChannelIdentity_RoundTripsThroughTheRepository()
    {
        var (visitorId, identityId) = await SeedVisitorAndActiveIdentityAsync();

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new VisitorRepository(db);
            var visitor = await repository.GetByIdAsync(visitorId, CancellationToken.None);
            visitor!.SetPreferredChannelIdentity(identityId);
            await repository.SaveAsync(visitor, CancellationToken.None);
        }

        await using var readDb = fixture.CreateDbContext();
        var found = await new VisitorRepository(readDb).GetByIdAsync(visitorId, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(identityId, found.PreferredChannelIdentityId);
    }

    [Fact]
    public async Task ClearingThePreference_PersistsAsNull()
    {
        var (visitorId, identityId) = await SeedVisitorAndActiveIdentityAsync();

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new VisitorRepository(db);
            var visitor = await repository.GetByIdAsync(visitorId, CancellationToken.None);
            visitor!.SetPreferredChannelIdentity(identityId);
            await repository.SaveAsync(visitor, CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new VisitorRepository(db);
            var visitor = await repository.GetByIdAsync(visitorId, CancellationToken.None);
            visitor!.SetPreferredChannelIdentity(null);
            await repository.SaveAsync(visitor, CancellationToken.None);
        }

        await using var readDb = fixture.CreateDbContext();
        var found = await new VisitorRepository(readDb).GetByIdAsync(visitorId, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Null(found.PreferredChannelIdentityId);
    }
}
