using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>2-06's backlog item, the migration half: Stage2PartitionMessages applies cleanly to a
/// fresh database (PostgresFixture runs every migration from scratch) and an insert landing in the
/// current month succeeds without needing PartitionMaintenanceJob to have run first - proving the
/// migration's own three initial partitions (current month + two) are real, not just documented.
/// The unique-constraint half lives in <see cref="MessageUniqueSequenceTests"/>.
///
/// <para>`13-06`: `retention_class` joins `created_at` as the second column a fresh insert must land
/// inside a real partition for - `'free'` here proves `Stage13RepartitionMessagesByRetentionClass`'s
/// own class-level bootstrap (one partition per <c>RetentionClass.KnownClasses</c>, each with the
/// current month plus two) is real on a from-scratch database, the same way this test always proved
/// the month-level bootstrap was.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MessagePartitioningTests(PostgresFixture fixture)
{
    [Fact]
    public async Task InsertingAMessage_WithCreatedAtInTheCurrentMonth_Succeeds()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Visitors.Add(new Visitor(visitorId, siteId, now));
            db.SaveChanges();
            db.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, now));
            await db.SaveChangesAsync();
        }

        var messageId = Guid.NewGuid();
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand("""
            insert into messages (id, conversation_id, sequence, author_kind, author_id, body, created_at, retention_class)
            values (@id, @conversationId, 1, 'Visitor', @authorId, 'hello', @now, 'free')
            """, connection))
        {
            command.Parameters.AddWithValue("id", messageId);
            command.Parameters.AddWithValue("conversationId", conversationId.Value);
            command.Parameters.AddWithValue("authorId", visitorId.Value);
            command.Parameters.AddWithValue("now", now);
            await command.ExecuteNonQueryAsync(); // throws if the current month has no partition
        }

        await using var verify = fixture.CreateDbContext();
        var stored = await verify.Set<Message>().SingleAsync(m => m.Id == new MessageId(messageId));
        Assert.Equal(conversationId, stored.ConversationId);
    }
}
