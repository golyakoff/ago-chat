using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `15-09`/`adr/0087`: `messages` is now `PARTITION BY HASH (site_id)`, 64 fixed buckets created once by
/// `Stage15RepartitionMessagesByTenantHash` and never again - there is no time dimension left in the
/// partition key at all. This is the proof that the standing failure mode this item exists to remove is
/// gone <b>structurally</b>, not patched: `2-06`'s original monthly grid (then `13-06`'s two-level
/// class/month grid) only ever created partitions looking <i>forward</i>, so any insert dated more than a
/// few months in the past - or run in the first days of a calendar month, before that cycle's own
/// partitions existed - failed with `23514: no partition of relation "messages_..." found for row`
/// (`adr/0087`'s own Context section: this broke `ago-chat`'s CI on 2026-09-01 and would have recurred
/// every month). A message dated <c>-95</c> days now inserts exactly as easily as one dated today,
/// because nothing about which bucket a row lands in depends on when it happened - only on
/// <c>hash(site_id)</c>, which does not change with the calendar. This test does <b>not</b> freeze a
/// clock to dodge the failure - it seeds a genuinely old date and asserts success regardless of what day
/// the suite happens to run on.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MessagePartitioningTests(PostgresFixture fixture)
{
    [Fact]
    public async Task InsertingAMessage_DatedWellIntoThePast_SucceedsRegardlessOfToday()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        // Well into the past - the old monthly grid created only the current month plus the next two,
        // so this date would have been rejected on some days and accepted on others depending on when
        // the suite ran. Under HASH(site_id) it is unconditionally accepted.
        var wellIntoThePast = now.AddDays(-95);

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
            insert into messages (id, conversation_id, sequence, author_kind, author_id, body, created_at, retention_class, site_id)
            values (@id, @conversationId, 1, 'Visitor', @authorId, 'hello', @createdAt, 'free', @siteId)
            """, connection))
        {
            command.Parameters.AddWithValue("id", messageId);
            command.Parameters.AddWithValue("conversationId", conversationId.Value);
            command.Parameters.AddWithValue("authorId", visitorId.Value);
            command.Parameters.AddWithValue("createdAt", wellIntoThePast);
            command.Parameters.AddWithValue("siteId", siteId.Value);
            // Throws "23514: no partition of relation ... found for row" against the old
            // RANGE(created_at) scheme unless the suite happens to run within the look-back stopgap's
            // window; under HASH(site_id) this always succeeds.
            await command.ExecuteNonQueryAsync();
        }

        await using var verify = fixture.CreateDbContext();
        var stored = await verify.Set<Message>().SingleAsync(m => m.Id == new MessageId(messageId));
        Assert.Equal(conversationId, stored.ConversationId);
        Assert.Equal(siteId, stored.SiteId);
    }

    [Fact]
    public async Task InsertingAMessage_WithCreatedAtToday_Succeeds()
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
            insert into messages (id, conversation_id, sequence, author_kind, author_id, body, created_at, retention_class, site_id)
            values (@id, @conversationId, 1, 'Visitor', @authorId, 'hello', @now, 'free', @siteId)
            """, connection))
        {
            command.Parameters.AddWithValue("id", messageId);
            command.Parameters.AddWithValue("conversationId", conversationId.Value);
            command.Parameters.AddWithValue("authorId", visitorId.Value);
            command.Parameters.AddWithValue("now", now);
            command.Parameters.AddWithValue("siteId", siteId.Value);
            await command.ExecuteNonQueryAsync();
        }

        await using var verify = fixture.CreateDbContext();
        var stored = await verify.Set<Message>().SingleAsync(m => m.Id == new MessageId(messageId));
        Assert.Equal(conversationId, stored.ConversationId);
    }
}
