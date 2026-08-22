using System.Diagnostics;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Pipeline;
using Ago.Chat.Module.Pipeline;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Concurrency.Tests;

/// <summary>
/// `4-05`'s Done-when, live: the real <c>Channel&lt;InboundMessage&gt;</c> -&gt;
/// <c>MessagePipelineWorkerHost</c> -&gt; <c>ConversationSequencer</c> -&gt; <c>BatchAccumulator</c>
/// -&gt; <c>BatchFlusherService</c> -&gt; <c>MessageBatchWriter</c> chain, against real Postgres. Each
/// test builds its own pipeline instance (never shared - `ChannelMessagePipeline` owns a bounded
/// channel that must start empty) via <see cref="CreatePipeline"/>.
/// </summary>
[Collection(ConcurrencyCollection.Name)]
public sealed class MessagePipelineTests(ConcurrencyTestFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GapFreeAscendingSequence_UnderConcurrentSendsToOneConversation_RepeatedUnderStress()
    {
        const int MessageCount = 50;

        for (var iteration = 0; iteration < 3; iteration++)
        {
            var (visitorId, conversationId) = await SeedConversationAsync();

            var (pipeline, workerHost, flusher, lifetime) = CreatePipeline(
                new MessagePipelineOptions { WorkerCount = 8 });
            await workerHost.StartAsync(CancellationToken.None);
            await flusher.StartAsync(CancellationToken.None);
            try
            {
                var sends = Enumerable.Range(0, MessageCount).Select(i => Task.Run(async () =>
                {
                    var pending = new PendingMessage(conversationId, MessageAuthorKind.Visitor, visitorId.Value, new MessageBody($"message {i}"));
                    return await pipeline.EnqueueAsync(pending, CancellationToken.None);
                }));
                var results = await Task.WhenAll(sends);
                Assert.All(results, r => Assert.True(r.IsSuccess, r.IsFailure ? r.Error!.Value.Message : ""));
            }
            finally
            {
                await StopPipelineAsync(lifetime, workerHost, flusher);
            }

            var sequences = await ReadSequencesAsync(conversationId);
            Assert.Equal(Enumerable.Range(1, MessageCount), sequences);
        }
    }

    [Fact]
    public async Task BatchWriter_ActuallyBatches_FewerTransactionsThanMessagesSent_UnderConcurrentLoad()
    {
        const int MessageCount = 30;
        var siteId = new SiteId(Guid.NewGuid());
        var conversations = new List<(VisitorId VisitorId, ConversationId ConversationId)>();

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            for (var i = 0; i < MessageCount; i++)
            {
                var visitorId = new VisitorId(Guid.NewGuid());
                var conversationId = new ConversationId(Guid.NewGuid());
                db.Visitors.Add(new Visitor(visitorId, siteId, Now));
                db.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, Now));
                conversations.Add((visitorId, conversationId));
            }

            await db.SaveChangesAsync();
        }

        // Every message is for a *different* conversation - ConversationSequencer only serialises
        // same-conversation work, so this is what lets all MessageCount sends actually be in flight,
        // and therefore batchable, at once. A generous BatchMaxRows/BatchMaxDelay so the whole burst
        // has room and time to land in one flush.
        var options = new MessagePipelineOptions { WorkerCount = 8, BatchMaxRows = MessageCount, BatchMaxDelay = TimeSpan.FromMilliseconds(500) };
        var (pipeline, workerHost, flusher, lifetime) = CreatePipeline(options);
        await workerHost.StartAsync(CancellationToken.None);
        await flusher.StartAsync(CancellationToken.None);
        try
        {
            var sends = conversations.Select(c => Task.Run(async () =>
            {
                var pending = new PendingMessage(c.ConversationId, MessageAuthorKind.Visitor, c.VisitorId.Value, new MessageBody("hello"));
                return await pipeline.EnqueueAsync(pending, CancellationToken.None);
            }));
            var results = await Task.WhenAll(sends);
            Assert.All(results, r => Assert.True(r.IsSuccess, r.IsFailure ? r.Error!.Value.Message : ""));
        }
        finally
        {
            await StopPipelineAsync(lifetime, workerHost, flusher);
        }

        var distinctTransactions = await CountDistinctTransactionsAsync(conversations.Select(c => c.ConversationId));
        Assert.True(
            distinctTransactions < MessageCount,
            $"Expected fewer transactions than messages sent ({MessageCount}); observed {distinctTransactions} distinct transaction(s).");
    }

    [Fact]
    public async Task Backpressure_WhenTheChannelStaysFull_BlocksUpToTheTimeout_ThenFailsExplicitly()
    {
        var (visitorId, conversationId) = await SeedConversationAsync();

        var options = new MessagePipelineOptions { ChannelCapacity = 1, EnqueueTimeout = TimeSpan.FromMilliseconds(300) };
        var lifetime = new FakeHostApplicationLifetime();
        var pipeline = new ChannelMessagePipeline(Options.Create(options), lifetime);
        // Deliberately no worker/flusher started - nothing ever drains the channel, so its one slot
        // stays occupied for the whole test, exactly the "stayed full" case being proven.

        using var fillCts = new CancellationTokenSource();
        var fillTask = pipeline.EnqueueAsync(
            new PendingMessage(conversationId, MessageAuthorKind.Visitor, visitorId.Value, new MessageBody("first")), fillCts.Token);

        // Give the first call's WriteAsync a moment to actually occupy the channel's one slot before
        // the second call below tries to saturate it further.
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        var stopwatch = Stopwatch.StartNew();
        var overflow = await pipeline.EnqueueAsync(
            new PendingMessage(conversationId, MessageAuthorKind.Visitor, visitorId.Value, new MessageBody("second")), CancellationToken.None);
        stopwatch.Stop();

        Assert.True(overflow.IsFailure);
        Assert.Equal("Message.Unavailable", overflow.Error!.Value.Code);
        // Author's decision (4-05's Scope): block up to the timeout, never an immediate reject and
        // never an unbounded wait - a near-zero elapsed time here would mean the second call gave up
        // instantly instead of actually waiting for the configured EnqueueTimeout.
        Assert.True(
            stopwatch.Elapsed >= TimeSpan.FromMilliseconds(200),
            $"Expected the call to block for close to the {options.EnqueueTimeout} timeout; only {stopwatch.Elapsed} elapsed.");

        fillCts.Cancel();
        await Assert.ThrowsAsync<TaskCanceledException>(() => fillTask);
        pipeline.Dispose();
    }

    [Fact]
    public async Task Shutdown_MessagesAlreadyQueuedWhenApplicationStoppingFires_AreDrainedAndCommitted_BeforeTheHostStops()
    {
        const int MessageCount = 20;
        var siteId = new SiteId(Guid.NewGuid());
        var conversations = new List<(VisitorId VisitorId, ConversationId ConversationId)>();

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            for (var i = 0; i < MessageCount; i++)
            {
                var visitorId = new VisitorId(Guid.NewGuid());
                var conversationId = new ConversationId(Guid.NewGuid());
                db.Visitors.Add(new Visitor(visitorId, siteId, Now));
                db.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, Now));
                conversations.Add((visitorId, conversationId));
            }

            await db.SaveChangesAsync();
        }

        // A short BatchMaxDelay so this final, never-followed-up batch flushes promptly rather than
        // this test waiting out a large configured delay; ShutdownDrainTimeout is comfortably longer
        // than that, so the drain itself is never what times out.
        var options = new MessagePipelineOptions
        {
            WorkerCount = 4,
            BatchMaxRows = 1000,
            BatchMaxDelay = TimeSpan.FromMilliseconds(200),
            ShutdownDrainTimeout = TimeSpan.FromSeconds(10),
        };
        var (pipeline, workerHost, flusher, lifetime) = CreatePipeline(options);
        await workerHost.StartAsync(CancellationToken.None);
        await flusher.StartAsync(CancellationToken.None);

        var sendTasks = conversations.Select(c => pipeline.EnqueueAsync(
            new PendingMessage(c.ConversationId, MessageAuthorKind.Visitor, c.VisitorId.Value, new MessageBody("hello")), CancellationToken.None)).ToArray();

        // A moment for the sends to actually reach the channel before shutdown starts - this test is
        // about work already queued surviving shutdown, not about a send racing shutdown itself
        // (ChannelMessagePipeline.EnqueueAsync's own ChannelClosedException handling, a separate,
        // already-covered behaviour, is what a send arriving *after* completion gets instead).
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        await StopPipelineAsync(lifetime, workerHost, flusher);

        var results = await Task.WhenAll(sendTasks);
        Assert.All(results, r => Assert.True(r.IsSuccess, r.IsFailure ? r.Error!.Value.Message : ""));

        await using var verify = fixture.CreateDbContext();
        var conversationIds = conversations.Select(c => c.ConversationId).ToList();
        var persistedCount = await verify.Set<Message>().CountAsync(m => conversationIds.Contains(m.ConversationId));
        Assert.Equal(MessageCount, persistedCount);
    }

    private async Task<(VisitorId VisitorId, ConversationId ConversationId)> SeedConversationAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(visitorId, siteId, Now));
        db.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, Now));
        await db.SaveChangesAsync();

        return (visitorId, conversationId);
    }

    private (ChannelMessagePipeline Pipeline, MessagePipelineWorkerHost WorkerHost, BatchFlusherService Flusher, FakeHostApplicationLifetime Lifetime)
        CreatePipeline(MessagePipelineOptions options)
    {
        var lifetime = new FakeHostApplicationLifetime();
        var pipeline = new ChannelMessagePipeline(Options.Create(options), lifetime);
        var sequencer = new ConversationSequencer();
        var accumulator = new BatchAccumulator();
        var writer = new MessageBatchWriter(fixture.DataSource, new SystemClock(), new UuidV7Generator(), NullLogger<MessageBatchWriter>.Instance);
        var workerHost = new MessagePipelineWorkerHost(
            pipeline, sequencer, accumulator, Options.Create(options), NullLogger<MessagePipelineWorkerHost>.Instance);
        var flusher = new BatchFlusherService(accumulator, writer, new SystemClock(), Options.Create(options));
        return (pipeline, workerHost, flusher, lifetime);
    }

    /// <summary>Fires ApplicationStopping (completing the pipeline's inbound channel) and waits for
    /// the whole drain - worker host first (it is what completes the accumulator's own channel once
    /// fully drained, see MessagePipelineWorkerHost.ExecuteAsync's remarks), then the flusher.</summary>
    private static async Task StopPipelineAsync(FakeHostApplicationLifetime lifetime, MessagePipelineWorkerHost workerHost, BatchFlusherService flusher)
    {
        lifetime.TriggerStopping();
        await workerHost.StopAsync(CancellationToken.None);
        await flusher.StopAsync(CancellationToken.None);
    }

    private async Task<List<int>> ReadSequencesAsync(ConversationId conversationId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT sequence FROM messages WHERE conversation_id = @conversationId ORDER BY sequence", connection);
        command.Parameters.AddWithValue("conversationId", conversationId.Value);
        await using var reader = await command.ExecuteReaderAsync();

        var sequences = new List<int>();
        while (await reader.ReadAsync())
        {
            sequences.Add(reader.GetInt32(0));
        }

        return sequences;
    }

    private async Task<int> CountDistinctTransactionsAsync(IEnumerable<ConversationId> conversationIds)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            // xid has no ordering operator, only equality - cast to text so COUNT(DISTINCT ...) can
            // still group by it.
            "SELECT COUNT(DISTINCT xmin::text) FROM messages WHERE conversation_id = ANY(@ids)", connection);
        command.Parameters.AddWithValue("ids", conversationIds.Select(c => c.Value).ToArray());
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
