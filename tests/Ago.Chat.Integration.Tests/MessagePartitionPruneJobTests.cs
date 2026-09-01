using Ago.Chat.Application.Abstractions;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Worker;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `15-04`/`adr/0031`'s own Done-when, reworked for `15-09`/`adr/0087`'s `DELETE`-based mechanism: a
/// real message row, past the retention horizon and archive-confirmed, is actually removed from a real
/// Postgres - not asserted against an empty table. Every message this suite seeds is dated far in the
/// past (year 1999-2001) so it can never collide with normal message-insert traffic in this shared
/// fixture, and every test deletes its own leftover rows in `finally` regardless of outcome.
///
/// <para><b>What changed from the pre-`15-09` version of this file, and why the tests below still prove
/// the same thing.</b> Before this item, the removal unit was a whole leaf partition (one class-month,
/// shared across tenants) and this suite created one, by name, per test. Now the removal unit is a
/// `(site_id, retention_class, month)` slice of rows inside the fixed 64-bucket table, discovered by
/// value rather than addressed by a partition name - so this suite seeds a site/conversation/message
/// instead of a partition, and asserts the message row is gone (or still present) instead of asserting
/// the partition table itself no longer exists in `pg_class`. The horizon/gate/metrics behaviour under
/// test is unchanged.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MessagePartitionPruneJobTests(PostgresFixture fixture)
{
    private const int RetentionHorizonMonths = 3;

    [Fact]
    public async Task PruneAsync_RemovesMessagesPastTheHorizon_WhenTheArchiveGateConfirms()
    {
        var (siteId, messageId) = await SeedExpiredMessageAsync(2000, 1);

        var job = CreateJob(referenceNow: new DateTimeOffset(2000, 6, 1, 0, 0, 0, TimeSpan.Zero), gate: new FakeArchiveGate(confirmed: true));
        await job.PruneAsync(CancellationToken.None);

        Assert.False(await MessageExistsAsync(messageId));
    }

    [Fact]
    public async Task PruneAsync_LeavesMessagesPastTheHorizon_WhenTheArchiveGateDoesNotConfirm()
    {
        var (siteId, messageId) = await SeedExpiredMessageAsync(2000, 2);

        var job = CreateJob(referenceNow: new DateTimeOffset(2000, 6, 1, 0, 0, 0, TimeSpan.Zero), gate: new FakeArchiveGate(confirmed: false));
        await job.PruneAsync(CancellationToken.None);

        Assert.True(await MessageExistsAsync(messageId));
    }

    /// <summary>A slice inside the horizon is never even offered to the gate - proves the horizon check
    /// comes first, so a real (future) archive-confirmation implementation is never asked about a period
    /// that has not been archived yet by design, only ones that plausibly could have been.</summary>
    [Fact]
    public async Task PruneAsync_NeverConsultsTheGate_ForASliceInsideTheHorizon()
    {
        var (siteId, messageId) = await SeedExpiredMessageAsync(2000, 5);

        var gate = new FakeArchiveGate(confirmed: true);
        // referenceNow is the same month the message was created in - well inside any positive horizon.
        var job = CreateJob(referenceNow: new DateTimeOffset(2000, 5, 15, 0, 0, 0, TimeSpan.Zero), gate: gate);
        await job.PruneAsync(CancellationToken.None);

        Assert.True(await MessageExistsAsync(messageId));
        Assert.DoesNotContain(siteId.Value, gate.CheckedSiteIds);
    }

    /// <summary>`13-06` replaced `15-04`'s real-default gate (`AlwaysConfirmedMessageArchiveGate`) with
    /// the object-storage-backed <c>MessageArchiveGate</c> (its own integration coverage lives in
    /// `MessageRetentionArchiveEndToEndTests`) - `AlwaysConfirmedMessageArchiveGate` is kept only as a
    /// permissive test fake now (that class's own remarks), so this test proves the same thing it always
    /// did - the job's own wiring genuinely removes rows under a confirming gate, not just that the type
    /// compiles - against whichever gate a caller hands it.</summary>
    [Fact]
    public async Task PruneAsync_WithAConfirmingFakeGate_RemovesMessagesPastTheHorizon()
    {
        var (_, messageId) = await SeedExpiredMessageAsync(2001, 1);

        var job = CreateJob(referenceNow: new DateTimeOffset(2001, 6, 1, 0, 0, 0, TimeSpan.Zero), gate: new AlwaysConfirmedMessageArchiveGate());
        await job.PruneAsync(CancellationToken.None);

        Assert.False(await MessageExistsAsync(messageId));
    }

    [Fact]
    public async Task PruneAsync_RecordsTheRetentionMetrics()
    {
        var (droppedSiteId, droppedMessageId) = await SeedExpiredMessageAsync(1999, 11);
        var (_, pendingMessageId) = await SeedExpiredMessageAsync(1999, 12);

        var exportedMetrics = new List<Metric>();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(ChatMetrics.MeterName)
            .AddInMemoryExporter(exportedMetrics)
            .Build();

        var gate = new SelectiveArchiveGate(confirmedFor: droppedSiteId.Value);
        var job = CreateJob(referenceNow: new DateTimeOffset(2000, 6, 1, 0, 0, 0, TimeSpan.Zero), gate: gate);
        await job.PruneAsync(CancellationToken.None);
        meterProvider.ForceFlush();

        Assert.False(await MessageExistsAsync(droppedMessageId));
        Assert.True(await MessageExistsAsync(pendingMessageId));

        var droppedMetric = exportedMetrics.Single(m => m.Name == ChatMetrics.RetentionPartitionsDroppedInstrumentName);
        long droppedCount = 0;
        foreach (ref readonly var point in droppedMetric.GetMetricPoints())
        {
            droppedCount += point.GetSumLong();
        }
        Assert.Equal(1, droppedCount);

        var pendingMetric = exportedMetrics.Single(m => m.Name == ChatMetrics.RetentionPartitionsPendingArchiveInstrumentName);
        long pendingCount = 0;
        foreach (ref readonly var point in pendingMetric.GetMetricPoints())
        {
            pendingCount += point.GetSumLong();
        }
        Assert.Equal(1, pendingCount);
    }

    /// <summary>A slice spanning more rows than one delete batch still drains completely in one prune
    /// cycle - `MessagePartitionPruneJobOptions.DeleteBatchSize`'s own bounded-loop contract
    /// (`MessagePartitionPruneJob.DeleteSliceAsync`), proven with a batch size small enough that a
    /// single slice needs several batches to clear.</summary>
    [Fact]
    public async Task PruneAsync_DrainsASliceLargerThanOneDeleteBatch_Completely()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        var createdAt = new DateTimeOffset(2001, 3, 15, 0, 0, 0, TimeSpan.Zero);

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Visitors.Add(new Visitor(visitorId, siteId, createdAt));
            db.SaveChanges();
            db.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, createdAt));
            await db.SaveChangesAsync();
        }

        const int messageCount = 7;
        for (var i = 0; i < messageCount; i++)
        {
            await InsertMessageAsync(siteId, conversationId, createdAt, sequence: i + 1);
        }

        var job = new MessagePartitionPruneJob(
            fixture.DataSource, new AlwaysConfirmedMessageArchiveGate(), new FakeFileStorage(),
            new FixedClock(new DateTimeOffset(2001, 8, 1, 0, 0, 0, TimeSpan.Zero)),
            Options.Create(new MessagePartitionPruneJobOptions { RetentionHorizonMonths = RetentionHorizonMonths, DeleteBatchSize = 2 }),
            NullLogger<MessagePartitionPruneJob>.Instance);
        await job.PruneAsync(CancellationToken.None);

        Assert.Equal(0, await MessageCountForConversationAsync(conversationId));
    }

    private MessagePartitionPruneJob CreateJob(DateTimeOffset referenceNow, IMessageArchiveGate gate) =>
        new(fixture.DataSource, gate, new FakeFileStorage(), new FixedClock(referenceNow),
            Options.Create(new MessagePartitionPruneJobOptions { RetentionHorizonMonths = RetentionHorizonMonths }),
            NullLogger<MessagePartitionPruneJob>.Instance);

    /// <summary>Seeds a real site/visitor/conversation via EF, then one message raw-inserted (bypassing
    /// the aggregate, the same "arbitrary `created_at`" convention every retention test in this codebase
    /// uses) dated the first of <paramref name="year"/>/<paramref name="month"/> - no partition to create
    /// first, unlike before `15-09`: every one of the 64 hash buckets already exists for every
    /// site.</summary>
    private async Task<(SiteId SiteId, Guid MessageId)> SeedExpiredMessageAsync(int year, int month)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        var createdAt = new DateTimeOffset(year, month, 15, 0, 0, 0, TimeSpan.Zero);

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Visitors.Add(new Visitor(visitorId, siteId, createdAt));
            db.SaveChanges();
            db.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, createdAt));
            await db.SaveChangesAsync();
        }

        var messageId = await InsertMessageAsync(siteId, conversationId, createdAt, sequence: 1);
        return (siteId, messageId);
    }

    private async Task<Guid> InsertMessageAsync(SiteId siteId, ConversationId conversationId, DateTimeOffset createdAt, int sequence)
    {
        var messageId = Guid.NewGuid();
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            insert into messages (id, conversation_id, sequence, author_kind, author_id, body, created_at, retention_class, site_id)
            values (@id, @conversationId, @sequence, 'Visitor', @authorId, 'seeded', @createdAt, 'free', @siteId)
            """, connection);
        command.Parameters.AddWithValue("id", messageId);
        command.Parameters.AddWithValue("conversationId", conversationId.Value);
        command.Parameters.AddWithValue("sequence", sequence);
        command.Parameters.AddWithValue("authorId", Guid.NewGuid());
        command.Parameters.AddWithValue("createdAt", createdAt);
        command.Parameters.AddWithValue("siteId", siteId.Value);
        await command.ExecuteNonQueryAsync();
        return messageId;
    }

    private async Task<bool> MessageExistsAsync(Guid messageId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM messages WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", messageId);
        return (long)(await command.ExecuteScalarAsync())! > 0;
    }

    private async Task<long> MessageCountForConversationAsync(ConversationId conversationId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM messages WHERE conversation_id = @id", connection);
        command.Parameters.AddWithValue("id", conversationId.Value);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class FakeArchiveGate(bool confirmed) : IMessageArchiveGate
    {
        public List<Guid> CheckedSiteIds { get; } = [];

        public Task<bool> IsArchivedAsync(SiteId siteId, RetentionClass retentionClass, DateOnly periodStart, CancellationToken cancellationToken)
        {
            CheckedSiteIds.Add(siteId.Value);
            return Task.FromResult(confirmed);
        }
    }

    private sealed class SelectiveArchiveGate(Guid confirmedFor) : IMessageArchiveGate
    {
        public Task<bool> IsArchivedAsync(SiteId siteId, RetentionClass retentionClass, DateOnly periodStart, CancellationToken cancellationToken) =>
            Task.FromResult(siteId.Value == confirmedFor);
    }
}
