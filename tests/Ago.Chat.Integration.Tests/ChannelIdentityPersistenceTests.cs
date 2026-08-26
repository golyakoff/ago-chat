using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `14-01`: <c>Stage14AddChannelIdentities</c> against a real Postgres, applied from scratch by
/// <see cref="PostgresFixture"/>'s own migration run - the same bar every other migration in
/// data-model.md's Migrations section was held to, rather than an assertion that the C# compiles.
///
/// <para>The claim worth proving here is specifically the <em>storage-level</em> one, because it is
/// the only one the Application-level tests cannot make: the unique index is what stops two processes
/// racing the very first inbound message from the same phone number and each creating its own
/// visitor. The Application-level resolve-then-create is the primary mechanism; this is the backstop,
/// the same division `adr/0019` draws for <c>messages</c>.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class ChannelIdentityPersistenceTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(
        DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    private async Task<(SiteId Site, VisitorId Visitor)> SeedSiteAndVisitorAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(visitorId, siteId, Now));
        await db.SaveChangesAsync();

        return (siteId, visitorId);
    }

    [Fact]
    public async Task AChannelIdentity_RoundTripsThroughTheRepository()
    {
        var (siteId, visitorId) = await SeedSiteAndVisitorAsync();
        var address = new ExternalChannelAddress($"+7{Random.Shared.NextInt64(1_000_000_000, 9_999_999_999)}");

        await using (var db = fixture.CreateDbContext())
        {
            await new ChannelIdentityRepository(db).SaveAsync(
                ChannelIdentity.Link(
                    new ChannelIdentityId(Guid.NewGuid()), siteId, ChannelKind.Sms, address, visitorId, Now),
                CancellationToken.None);
        }

        await using var readDb = fixture.CreateDbContext();
        var found = await new ChannelIdentityRepository(readDb)
            .FindAsync(siteId, ChannelKind.Sms, address, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(visitorId, found.VisitorId);
        Assert.Equal(address, found.Address);
        Assert.Equal(Now, found.FirstSeenAt);
        // The value object survived the round trip as a value object, not as a raw string that
        // happens to compare equal - ExternalChannelAddressConverter's read direction runs the
        // constructor, so an invalid row could never have materialized at all.
        Assert.Equal(address.Value, found.Address.Value);
    }

    [Fact]
    public async Task TheSameAddressOnAnotherChannel_IsADistinctRow()
    {
        var (siteId, visitorId) = await SeedSiteAndVisitorAsync();
        var address = new ExternalChannelAddress($"shared-{Guid.NewGuid():N}");

        await using var db = fixture.CreateDbContext();
        var repository = new ChannelIdentityRepository(db);
        await repository.SaveAsync(
            ChannelIdentity.Link(new ChannelIdentityId(Guid.NewGuid()), siteId, ChannelKind.Sms, address, visitorId, Now),
            CancellationToken.None);
        await repository.SaveAsync(
            ChannelIdentity.Link(new ChannelIdentityId(Guid.NewGuid()), siteId, ChannelKind.Telegram, address, visitorId, Now),
            CancellationToken.None);

        Assert.NotNull(await repository.FindAsync(siteId, ChannelKind.Sms, address, CancellationToken.None));
        Assert.NotNull(await repository.FindAsync(siteId, ChannelKind.Telegram, address, CancellationToken.None));
        Assert.Null(await repository.FindAsync(siteId, ChannelKind.Max, address, CancellationToken.None));
    }

    /// <summary>
    /// The backstop. Written as a raw insert, deliberately bypassing the repository's own
    /// resolve-then-create, because the point is exactly that the database catches what two racing
    /// processes could get past the application - the same reasoning
    /// <see cref="MessageUniqueSequenceTests"/> gives for its own raw insert.
    /// </summary>
    [Fact]
    public async Task TwoIdentitiesForTheSameSiteChannelAndAddress_TheSecondIsRejected()
    {
        var (siteId, visitorId) = await SeedSiteAndVisitorAsync();
        var address = $"+7{Random.Shared.NextInt64(1_000_000_000, 9_999_999_999)}";

        await using var connection = await fixture.DataSource.OpenConnectionAsync();

        const string sql = """
            insert into channel_identities
                (id, site_id, kind, external_address, visitor_id, first_seen_at, last_seen_at)
            values (@id, @siteId, 'Sms', @address, @visitorId, @now, @now)
            """;

        await using (var first = new NpgsqlCommand(sql, connection))
        {
            first.Parameters.AddWithValue("id", Guid.NewGuid());
            first.Parameters.AddWithValue("siteId", siteId.Value);
            first.Parameters.AddWithValue("address", address);
            first.Parameters.AddWithValue("visitorId", visitorId.Value);
            first.Parameters.AddWithValue("now", Now);
            await first.ExecuteNonQueryAsync();
        }

        await using var second = new NpgsqlCommand(sql, connection);
        second.Parameters.AddWithValue("id", Guid.NewGuid());
        second.Parameters.AddWithValue("siteId", siteId.Value);
        second.Parameters.AddWithValue("address", address);
        second.Parameters.AddWithValue("visitorId", visitorId.Value);
        second.Parameters.AddWithValue("now", Now);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => second.ExecuteNonQueryAsync());
        Assert.Equal("23505", exception.SqlState); // unique_violation
    }
}
