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
    // Real time, not a fixed date: 2-06 partitions messages by created_at, and the migration only
    // ever creates the current month's partition plus the next two (whenever it runs) - a fixed
    // past date would fall outside every partition that exists and fail with "no partition found
    // for row" instead of the unique-violation this test means to prove. Truncated to whole
    // seconds so it round-trips through Postgres's timestamptz (microsecond precision) unchanged,
    // same as the literal it replaces.
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

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

        // `13-06`: retention_class joined created_at in the widened unique index - the same literal
        // class both inserts so this still collides on all four columns, proving the constraint
        // itself rather than a class mismatch.
        const string sql = """
            insert into messages (id, conversation_id, sequence, author_kind, author_id, body, created_at, retention_class)
            values (@id, @conversationId, 1, 'Visitor', @authorId, 'dup', @now, 'free')
            """;

        await using (var first = new NpgsqlCommand(sql, connection))
        {
            first.Parameters.AddWithValue("id", Guid.NewGuid());
            first.Parameters.AddWithValue("conversationId", conversationId.Value);
            first.Parameters.AddWithValue("authorId", visitorId.Value);
            first.Parameters.AddWithValue("now", Now);
            await first.ExecuteNonQueryAsync();
        }

        await using var second = new NpgsqlCommand(sql, connection);
        second.Parameters.AddWithValue("id", Guid.NewGuid());
        second.Parameters.AddWithValue("conversationId", conversationId.Value);
        second.Parameters.AddWithValue("authorId", visitorId.Value);
        second.Parameters.AddWithValue("now", Now);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => second.ExecuteNonQueryAsync());
        Assert.Equal("23505", exception.SqlState); // unique_violation
    }
}
