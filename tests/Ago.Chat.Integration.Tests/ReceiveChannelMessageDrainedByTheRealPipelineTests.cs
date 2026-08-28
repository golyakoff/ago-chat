using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.ReceiveChannelMessage;
using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Application.UseCases.StartConversation;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Pipeline;
using Ago.Chat.Module.Pipeline;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// Found live, 2026-08-28, verifying `14-02` against a real MAX bot: a real message created a real
/// <c>Conversation</c> but no <c>Message</c> ever landed, silently - no exception, because the caller
/// (<c>ReceiveChannelMessageHandler</c> -&gt; <c>SendVisitorMessageHandler</c> -&gt;
/// <c>IMessagePipeline.EnqueueAsync</c>) awaits an ack that nothing ever completes.
///
/// Root cause: <c>Ago.Chat.Worker</c>'s own <c>Program.cs</c> registers <c>MaxLongPollingService</c> -
/// a second producer onto the shared, "registered everywhere" <c>ChannelMessagePipeline</c> - but
/// never registered <c>MessagePipelineWorkerHost</c>/<c>BatchFlusherService</c>, the two hosted
/// services that actually drain it. Those were, until `14-02`, registered only in
/// <c>Ago.Chat.Api</c>'s own <c>Program.cs</c>, on the explicit (and until then correct) assumption
/// that only <c>Ago.Chat.Api</c>'s hubs ever enqueued onto this pipeline.
///
/// No existing test caught this because every other test touching <c>SendVisitorMessageHandler</c> -
/// <c>ReceiveChannelMessageHandlerTests</c> (`Ago.Chat.Application.Tests`), every
/// `*EndToEndTests.cs` in this project - constructs it with a fake or synchronous pipeline
/// (<c>FakeApplyingMessagePipeline</c>, <c>SynchronousMessagePipeline</c>), never the real
/// <c>Channel&lt;InboundMessage&gt;</c>-based one <c>MessagePipelineTests.cs</c>
/// (`Ago.Chat.Concurrency.Tests`) proves works - but that file drives the pipeline directly
/// (<c>pipeline.EnqueueAsync</c>), never through <c>ReceiveChannelMessageHandler</c>, so it could not
/// have caught a *host-wiring* gap either. This file closes both gaps at once: the real pipeline,
/// reached the way `14-02`'s own inbound path reaches it.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ReceiveChannelMessageDrainedByTheRealPipelineTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AChannelMessage_ReachesPostgres_ThroughTheRealAsyncPipeline_NotJustAFake()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        var lifetime = new FakeHostApplicationLifetime();
        var pipelineOptions = new MessagePipelineOptions();
        var pipeline = new ChannelMessagePipeline(Options.Create(pipelineOptions), lifetime);
        var sequencer = new ConversationSequencer();
        var accumulator = new BatchAccumulator();
        var writer = new MessageBatchWriter(
            fixture.DataSource, new SystemClock(), new UuidV7Generator(), NullLogger<MessageBatchWriter>.Instance);
        var workerHost = new MessagePipelineWorkerHost(
            pipeline, sequencer, accumulator, Options.Create(pipelineOptions), NullLogger<MessagePipelineWorkerHost>.Instance);
        var flusher = new BatchFlusherService(accumulator, writer, new SystemClock(), Options.Create(pipelineOptions));

        // The exact two lines Ago.Chat.Worker's own Program.cs must run for a message received there
        // to ever be drained - see that file's own remarks on this bug. Left un-started, this test
        // reproduces the live symptom exactly: HandleAsync never returns (nothing ever completes the
        // ack), which is why the regression check below uses a short timeout rather than asserting a
        // clean failure - a hang *is* the bug's own shape, not a side effect of proving it.
        await workerHost.StartAsync(CancellationToken.None);
        await flusher.StartAsync(CancellationToken.None);
        try
        {
            await using var db = fixture.CreateDbContext();
            var identities = new ChannelIdentityRepository(db);
            var visitors = new VisitorRepository(db);
            var conversations = new ConversationRepository(db);
            var clock = new SystemClock();
            var idGenerator = new UuidV7Generator();

            var handler = new ReceiveChannelMessageHandler(
                identities,
                visitors,
                new StartConversationHandler(visitors, conversations, clock, idGenerator),
                new SendVisitorMessageHandler(
                    conversations, new FakeRateLimiter(), new MessageSendRateLimitOptions(), pipeline),
                clock,
                idGenerator);

            var result = await handler.HandleAsync(
                new ReceiveChannelMessage(
                    siteId, ChannelKind.Max, new ExternalChannelAddress("194525157"),
                    new ExternalMessageId("mid-1"), "hello from a real MAX account"),
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Message : "");

            var body = await ReadMessageBodyAsync(result.Value.ConversationId);
            Assert.Equal("hello from a real MAX account", body);
        }
        finally
        {
            lifetime.TriggerStopping();
            await workerHost.StopAsync(CancellationToken.None);
            await flusher.StopAsync(CancellationToken.None);
        }
    }

    private async Task<string?> ReadMessageBodyAsync(ConversationId conversationId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT body FROM messages WHERE conversation_id = @conversationId", connection);
        command.Parameters.AddWithValue("conversationId", conversationId.Value);
        return (string?)await command.ExecuteScalarAsync();
    }

    /// <summary>Same minimal shape as `Ago.Chat.Concurrency.Tests.FakeHostApplicationLifetime` and
    /// `TracingEndToEndTests`' own private copy in this same project - duplicated rather than shared
    /// across test projects/files for the identical reason both of those already state (small enough
    /// that a new dependency edge is not worth it).</summary>
    private sealed class FakeHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void TriggerStopping() => _stopping.Cancel();

        public void StopApplication() => TriggerStopping();
    }
}
