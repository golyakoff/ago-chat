using Ago.Chat.Domain;
using Ago.Chat.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `18-01`: real Postgres, real "legacy" rows with a `NULL site_id` - seeded with a raw `INSERT`
/// bypassing the domain model entirely (the ordinary write path, `Conversation.AddVisitorMessage`,
/// always stamps `site_id` now, so it is the one thing that cannot produce the row this job exists to
/// fix). Every conversation/message here is seeded directly against the current month's partition
/// (real time, not a fixed date - <c>ConversationReadStoreTests</c>' own precedent: only the current
/// month and the next two are guaranteed present in a fresh container), so no partition bookkeeping of
/// its own is needed the way <c>MessageSearchIndexJobTests</c>'s far-past partitions require.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MessageSiteIdBackfillJobTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    [Fact]
    public async Task BackfillAsync_FillsInSiteIdForALegacyRow_FromItsOwningConversation()
    {
        var (siteId, conversationId, messageId) = await SeedLegacyRowAsync();
        var job = CreateJob();

        await job.BackfillAsync(CancellationToken.None);

        Assert.Equal(siteId.Value, await ReadSiteIdAsync(messageId));
        _ = conversationId;
    }

    [Fact]
    public async Task BackfillAsync_NeverTouchesARowThatAlreadyHasASiteId()
    {
        // The ordinary path: Conversation.AddVisitorMessage already stamps site_id, so this row is
        // never a backfill candidate - the job's WHERE site_id IS NULL must leave it alone.
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, Now);
        var message = conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Visitors.Add(new Visitor(visitorId, siteId, Now));
            db.Conversations.Add(conversation);
            await db.SaveChangesAsync();
        }

        var job = CreateJob();
        await job.BackfillAsync(CancellationToken.None);

        Assert.Equal(siteId.Value, await ReadSiteIdAsync(message.Id));
    }

    /// <summary>Bounded batches, proven rather than assumed: a `BatchSize` of 1 against 3 legacy rows
    /// still backfills every one of them, in more than one pass.</summary>
    [Fact]
    public async Task BackfillAsync_WithABatchSizeSmallerThanTheBacklog_StillBackfillsEveryRow()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var conversationId = await SeedConversationAsync(siteId);
        var messageIds = new List<MessageId>();
        for (var i = 0; i < 3; i++)
        {
            messageIds.Add(await SeedLegacyMessageAsync(conversationId, i + 1, $"legacy message {i}"));
        }

        var job = CreateJob(batchSize: 1);
        await job.BackfillAsync(CancellationToken.None);

        foreach (var messageId in messageIds)
        {
            Assert.Equal(siteId.Value, await ReadSiteIdAsync(messageId));
        }
    }

    [Fact]
    public async Task BackfillAsync_RunTwice_IsIdempotent()
    {
        var (siteId, _, messageId) = await SeedLegacyRowAsync();
        var job = CreateJob();

        await job.BackfillAsync(CancellationToken.None);
        await job.BackfillAsync(CancellationToken.None);

        Assert.Equal(siteId.Value, await ReadSiteIdAsync(messageId));
    }

    private MessageSiteIdBackfillJob CreateJob(int batchSize = 500) =>
        new(fixture.DataSource,
            Options.Create(new MessageSiteIdBackfillJobOptions { Interval = TimeSpan.FromMinutes(10), BatchSize = batchSize }),
            NullLogger<MessageSiteIdBackfillJob>.Instance);

    private async Task<(SiteId SiteId, ConversationId ConversationId, MessageId MessageId)> SeedLegacyRowAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var conversationId = await SeedConversationAsync(siteId);
        var messageId = await SeedLegacyMessageAsync(conversationId, 1, "legacy message, no site_id");
        return (siteId, conversationId, messageId);
    }

    private async Task<ConversationId> SeedConversationAsync(SiteId siteId)
    {
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, Now);

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(visitorId, siteId, Now));
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();

        return conversation.Id;
    }

    /// <summary>Bypasses `Conversation`/EF entirely - a raw `INSERT` with no `site_id`, the only way
    /// left to produce the exact row shape this job exists to fix, now that the ordinary write path
    /// always stamps one.</summary>
    private async Task<MessageId> SeedLegacyMessageAsync(ConversationId conversationId, int sequence, string body)
    {
        var messageId = new MessageId(Guid.NewGuid());

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        const string sql = """
            INSERT INTO messages (id, conversation_id, sequence, author_kind, author_id, body, created_at, site_id)
            VALUES (@Id, @ConversationId, @Sequence, 'Visitor', @AuthorId, @Body, @CreatedAt, NULL)
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("Id", messageId.Value);
        command.Parameters.AddWithValue("ConversationId", conversationId.Value);
        command.Parameters.AddWithValue("Sequence", sequence);
        command.Parameters.AddWithValue("AuthorId", Guid.NewGuid());
        command.Parameters.AddWithValue("Body", body);
        command.Parameters.AddWithValue("CreatedAt", Now.UtcDateTime);
        await command.ExecuteNonQueryAsync();

        return messageId;
    }

    private async Task<Guid?> ReadSiteIdAsync(MessageId messageId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT site_id FROM messages WHERE id = @Id", connection);
        command.Parameters.AddWithValue("Id", messageId.Value);
        var result = await command.ExecuteScalarAsync();
        return result is DBNull or null ? null : (Guid)result;
    }
}
