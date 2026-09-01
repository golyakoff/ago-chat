using Ago.Chat.Domain;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>data-model.md: the unique (conversation_id, sequence) constraint turns duplicate
/// delivery into a no-op insert "at the storage level" - proven here with a raw duplicate insert,
/// bypassing Domain's own sequence-increment entirely, since the point is that the database catches
/// what a retry or a race could get past the application.</summary>
[Collection(PostgresCollection.Name)]
public class MessageUniqueSequenceTests(PostgresFixture fixture)
{
    // `15-09`/`adr/0087`: any timestamp works now - `messages` is `PARTITION BY HASH (site_id)`, not
    // `RANGE (created_at)`, so there is no "no partition found for row" failure mode left to dodge by
    // using real time instead of a fixed date. Kept as a fixed instant anyway (simpler, deterministic),
    // truncated to whole seconds so it round-trips through Postgres's timestamptz unchanged.
    private static readonly DateTimeOffset Now = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task InsertingTwoMessagesWithTheSameConversationAndSequence_TheSecondIsRejected()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Visitors.Add(new Visitor(visitorId, siteId, Now));
            db.SaveChanges();
            db.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, Now));
            await db.SaveChangesAsync();
        }

        await using var connection = await fixture.DataSource.OpenConnectionAsync();

        // `15-09`/`adr/0087`: the widened unique index is now (conversation_id, sequence, site_id) -
        // created_at/retention_class dropped out of the partition key, site_id took their place. The
        // same literal site_id on both inserts is what makes this still collide, proving the
        // constraint itself rather than a tenant mismatch.
        const string sql = """
            insert into messages (id, conversation_id, sequence, author_kind, author_id, body, created_at, retention_class, site_id)
            values (@id, @conversationId, 1, 'Visitor', @authorId, 'dup', @now, 'free', @siteId)
            """;

        await using (var first = new NpgsqlCommand(sql, connection))
        {
            first.Parameters.AddWithValue("id", Guid.NewGuid());
            first.Parameters.AddWithValue("conversationId", conversationId.Value);
            first.Parameters.AddWithValue("authorId", visitorId.Value);
            first.Parameters.AddWithValue("now", Now);
            first.Parameters.AddWithValue("siteId", siteId.Value);
            await first.ExecuteNonQueryAsync();
        }

        await using var second = new NpgsqlCommand(sql, connection);
        second.Parameters.AddWithValue("id", Guid.NewGuid());
        second.Parameters.AddWithValue("conversationId", conversationId.Value);
        second.Parameters.AddWithValue("authorId", visitorId.Value);
        second.Parameters.AddWithValue("now", Now);
        second.Parameters.AddWithValue("siteId", siteId.Value);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => second.ExecuteNonQueryAsync());
        Assert.Equal("23505", exception.SqlState); // unique_violation
    }
}
