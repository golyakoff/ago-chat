using System.Collections.Concurrent;
using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Worker;
using Ago.Platform.Abstractions;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Messaging.RabbitMq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace Ago.Chat.Integration.Tests;

/// <summary>2-04: real Postgres, real RabbitMQ, no mocking either (testing.md). Each test builds its
/// own <see cref="OutboxDispatcher"/> and starts/stops it directly via the public
/// <see cref="BackgroundService"/> API - the same lifecycle a real host drives.</summary>
[Collection(OutboxDispatcherCollection.Name)]
public sealed class OutboxDispatcherTests(OutboxDispatcherFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RowWrittenByTheRealHandlerPath_IsPublishedAndMarked()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());

        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            seed.Visitors.Add(new Visitor(visitorId, siteId, Now));
            seed.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, Now));
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        Guid messageId;
        await using (var db = fixture.CreateDbContext())
        {
            var handler = new SendVisitorMessageHandler(
                new Ago.Chat.Infrastructure.Postgres.ConversationRepository(db),
                new FakeRateLimiter(),
                new Ago.Chat.Application.UseCases.SendMessage.MessageSendRateLimitOptions(),
                new SynchronousMessagePipeline(fixture.DataSource));

            var result = await handler.HandleAsync(new SendVisitorMessage(conversationId, visitorId, "hello"), CancellationToken.None);
            Assert.True(result.IsSuccess);

            await using var verify = fixture.CreateDbContext();
            messageId = (await verify.Set<Ago.Platform.Persistence.Postgres.OutboxMessage>().SingleAsync(CancellationToken.None)).Id;
        }

        await using var connection = fixture.CreateRabbitMqConnection();
        var consumer = new RabbitMqEventConsumer(connection);
        var received = new ConcurrentBag<EventEnvelope>();
        await consumer.SubscribeAsync(
            "MessageAccepted", SubscriptionMode.Broadcast, "test-consumer", new RetryPolicy(3, TimeSpan.FromMilliseconds(200), $"dlq.{Guid.NewGuid():N}"),
            (envelope, ctx, ct) => { received.Add(envelope); return ctx.AckAsync(ct); }, CancellationToken.None);

        var dispatcher = CreateDispatcher(connection);
        await dispatcher.StartAsync(CancellationToken.None);
        try
        {
            await OutboxTestHelpers.WaitUntilAsync(() => !received.IsEmpty, TimeSpan.FromSeconds(10));
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }

        var envelope = Assert.Single(received);
        Assert.Equal(messageId, envelope.MessageId);

        await using var verifyPublished = fixture.CreateDbContext();
        var row = await verifyPublished.Set<Ago.Platform.Persistence.Postgres.OutboxMessage>().SingleAsync(o => o.Id == messageId, CancellationToken.None);
        Assert.NotNull(row.PublishedAt);
    }

    [Fact]
    public async Task TwoDispatchers_RacingForTheSameBatch_EachRowPublishedExactlyOnce()
    {
        const int rowCount = 20;
        var ids = await SeedOutboxRowsAsync(rowCount);

        await using var connectionA = fixture.CreateRabbitMqConnection();
        await using var connectionB = fixture.CreateRabbitMqConnection();
        var consumerConnection = fixture.CreateRabbitMqConnection();
        await using var _ = consumerConnection;

        var consumer = new RabbitMqEventConsumer(consumerConnection);
        var receivedIds = new ConcurrentBag<Guid>();
        await consumer.SubscribeAsync(
            "MessageAccepted", SubscriptionMode.Broadcast, "test-consumer", new RetryPolicy(3, TimeSpan.FromMilliseconds(200), $"dlq.{Guid.NewGuid():N}"),
            (envelope, ctx, ct) => { receivedIds.Add(envelope.MessageId); return ctx.AckAsync(ct); }, CancellationToken.None);

        var dispatcherA = CreateDispatcher(connectionA);
        var dispatcherB = CreateDispatcher(connectionB);
        await dispatcherA.StartAsync(CancellationToken.None);
        await dispatcherB.StartAsync(CancellationToken.None);
        try
        {
            await OutboxTestHelpers.WaitUntilAsync(async () => await AllPublishedAsync(ids), TimeSpan.FromSeconds(15));
        }
        finally
        {
            await dispatcherA.StopAsync(CancellationToken.None);
            await dispatcherB.StopAsync(CancellationToken.None);
        }

        await OutboxTestHelpers.WaitUntilAsync(() => receivedIds.Count >= rowCount, TimeSpan.FromSeconds(5));

        Assert.True(await AllPublishedAsync(ids));
        Assert.Equal(rowCount, receivedIds.Count); // no id delivered more than once in the broadcast either
        Assert.Equal(rowCount, receivedIds.Distinct().Count());
    }

    [Fact]
    public async Task ListenNotify_WakesTheDispatcher_MuchFasterThanTheFallbackPoll()
    {
        var options = Options.Create(new OutboxDispatcherOptions { PollInterval = TimeSpan.FromSeconds(30), BatchSize = 20 });
        await using var connection = fixture.CreateRabbitMqConnection();
        var dispatcher = new OutboxDispatcher(fixture.DataSource, new RabbitMqEventPublisher(connection, NullLogger<RabbitMqEventPublisher>.Instance), new SystemClock(), options, NullLogger<OutboxDispatcher>.Instance);

        await dispatcher.StartAsync(CancellationToken.None);
        try
        {
            // Give the LISTEN registration a moment to land before the row is inserted.
            await Task.Delay(500);

            var id = (await SeedOutboxRowsAsync(1))[0];
            var started = DateTimeOffset.UtcNow;

            var published = await OutboxTestHelpers.WaitUntilAsync(() => IsPublishedAsync(id), TimeSpan.FromSeconds(10));
            var elapsed = DateTimeOffset.UtcNow - started;

            Assert.True(published);
            Assert.True(elapsed < TimeSpan.FromSeconds(5), $"Expected NOTIFY-driven dispatch well under the 30s poll interval, took {elapsed}.");
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// `7-02`'s Done-when: proves a real value change for the outbox-lag gauge - seeds a row with an
    /// occurred_at well in the past, runs one dispatch cycle directly (not through the
    /// BackgroundService loop, so timing is deterministic), and asserts ChatMetrics' gauge reports a
    /// lag at least as large as the age seeded, not merely that the instrument exists.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this observes more than once.</b> The gauge reads a <i>process-global</i> static
    /// slot (<c>ChatMetrics.SetOutboxLagSeconds</c>, and that class's own remarks explain why it is a
    /// slot rather than a per-instance <c>ObservableGauge</c>). In production that is unambiguous:
    /// one host process runs exactly one <c>OutboxDispatcher</c>, so the only writer is the only
    /// dispatcher. This test assembly deliberately breaks that assumption - at least five other
    /// classes (<c>ConnectionFanoutEndToEndTests</c>, <c>ConversationAssignmentFanoutEndToEndTests</c>,
    /// <c>TracingEndToEndTests</c>, <c>AttachmentThumbnailEndToEndTests</c>,
    /// <c>UnreadCounterEndToEndTests</c>) start a real dispatcher and leave it polling on a 2s
    /// interval while they wait for an end-to-end path to complete, and several of them declare no
    /// <c>[Collection]</c> at all, so xUnit runs them in parallel with this collection.</para>
    /// <para>Every one of those poll cycles that claims nothing writes <c>0</c> into the same slot -
    /// correctly, per <c>OutboxDispatcher</c>'s own reasoning that an empty claim means no lag rather
    /// than unknown. So a foreign <c>0</c> can land between this test's <c>DispatchBatchAsync</c> and
    /// its <c>ForceFlush</c>, and the assertion sees <c>0s</c> for a row it definitely seeded. That is
    /// exactly how this test failed on `main` on 2026-08-25, and twice intermittently before that; it
    /// passes in isolation every time, which is the signature of the interference rather than of a
    /// defect in the dispatcher.</para>
    /// <para>Re-observing is the proportionate fix. The alternative considered was putting all six
    /// dispatcher-running classes into one xUnit collection, which would restore the production
    /// "one dispatcher at a time" invariant honestly - but it serialises six container-backed
    /// end-to-end classes that currently run in parallel, turning the suite's wall clock from a max
    /// into a sum, to make one metrics assertion deterministic. Each attempt here re-seeds its own
    /// row and re-dispatches, so a genuinely broken gauge still fails every attempt and the test
    /// fails loudly; only foreign interference is retried past.</para>
    /// </remarks>
    [Fact]
    public async Task DispatchBatchAsync_WithAnOldUnpublishedRow_MovesTheOutboxLagGauge()
    {
        const int attempts = 3;
        double lagSeconds = 0;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            lagSeconds = await ObserveOutboxLagGaugeAfterOneDispatchAsync();
            if (lagSeconds >= 25)
            {
                return;
            }
        }

        Assert.Fail(
            $"Expected the lag gauge to reflect the ~30s-old seeded row; observed {lagSeconds}s on each of " +
            $"{attempts} attempts. A single 0 is usually a parallel dispatcher overwriting the shared slot " +
            $"(see this test's remarks); {attempts} in a row means the dispatcher is not reporting lag at all.");
    }

    /// <summary>Seeds one ~30s-old outbox row, runs exactly one dispatch cycle, and returns whatever
    /// the outbox-lag gauge reports immediately afterwards. The row is always deleted again.</summary>
    private async Task<double> ObserveOutboxLagGaugeAfterOneDispatchAsync()
    {
        var exportedMetrics = new List<Metric>();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(ChatMetrics.MeterName)
            .AddInMemoryExporter(exportedMetrics)
            .Build();

        // This fixture's Postgres/RabbitMQ containers are shared across every test in this
        // collection (OutboxDispatcherFixture's own remarks), with no per-test reset - a row this
        // test inserts and successfully publishes must be deleted again once assertions are done, or
        // it permanently pollutes sibling tests that assume an otherwise-empty outbox table
        // (RowWrittenByTheRealHandlerPath_IsPublishedAndMarked's own SingleAsync() over the whole
        // table) and a real dispatcher elsewhere in the suite could pick it up and publish it a
        // second time (TwoDispatchers_RacingForTheSameBatch's own exact-count assertion). That
        // applies per attempt, which is why the delete is in this helper's own finally rather than
        // around the retry loop.
        var occurredAt = DateTimeOffset.UtcNow.AddSeconds(-30);
        var rowId = Guid.NewGuid();
        await using (var db = fixture.CreateDbContext())
        {
            db.Add(new Ago.Platform.Persistence.Postgres.OutboxMessage(
                rowId, occurredAt, "MessageAccepted", 1, "{}", $"key-{Guid.NewGuid():N}", Guid.NewGuid()));
            await db.SaveChangesAsync(CancellationToken.None);
        }

        try
        {
            await using var connection = fixture.CreateRabbitMqConnection();
            var dispatcher = CreateDispatcher(connection);
            await dispatcher.DispatchBatchAsync(CancellationToken.None);

            meterProvider.ForceFlush();
            var gauge = exportedMetrics.Single(m => m.Name == ChatMetrics.OutboxLagInstrumentName);
            double lagSeconds = 0;
            foreach (ref readonly var point in gauge.GetMetricPoints())
            {
                lagSeconds = point.GetGaugeLastValueDouble();
            }

            return lagSeconds;
        }
        finally
        {
            await DeleteOutboxRowAsync(rowId);
        }
    }

    /// <summary>
    /// `7-02`'s Done-when for the publish-failure counter - a publisher that always throws forces
    /// every claimed row down the failure path, proving the counter moves on a real failure rather
    /// than just existing.
    /// </summary>
    [Fact]
    public async Task DispatchBatchAsync_WhenPublishingThrows_RecordsAPublishFailure()
    {
        var exportedMetrics = new List<Metric>();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(ChatMetrics.MeterName)
            .AddInMemoryExporter(exportedMetrics)
            .Build();

        // Same shared-fixture cleanup obligation as the lag-gauge test above - this row is never
        // marked published (the publisher always throws), so it must be deleted explicitly rather
        // than relying on published_at ever being set.
        var rowId = Guid.NewGuid();
        await using (var db = fixture.CreateDbContext())
        {
            db.Add(new Ago.Platform.Persistence.Postgres.OutboxMessage(
                rowId, DateTimeOffset.UtcNow, "MessageAccepted", 1, "{}", $"key-{Guid.NewGuid():N}", Guid.NewGuid()));
            await db.SaveChangesAsync(CancellationToken.None);
        }

        try
        {
            var dispatcher = new OutboxDispatcher(
                fixture.DataSource, new ThrowingEventPublisher(), new SystemClock(),
                Options.Create(new OutboxDispatcherOptions { PollInterval = TimeSpan.FromSeconds(2), BatchSize = 20 }),
                NullLogger<OutboxDispatcher>.Instance);

            await dispatcher.DispatchBatchAsync(CancellationToken.None);

            meterProvider.ForceFlush();
            var failures = exportedMetrics.Single(m => m.Name == ChatMetrics.OutboxPublishFailuresInstrumentName);
            long total = 0;
            foreach (ref readonly var point in failures.GetMetricPoints())
            {
                total += point.GetSumLong();
            }

            Assert.Equal(1, total);
        }
        finally
        {
            await DeleteOutboxRowAsync(rowId);
        }
    }

    private async Task DeleteOutboxRowAsync(Guid id)
    {
        await using var db = fixture.CreateDbContext();
        var row = await db.Set<Ago.Platform.Persistence.Postgres.OutboxMessage>().FindAsync([id], CancellationToken.None);
        if (row is not null)
        {
            db.Remove(row);
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private sealed class ThrowingEventPublisher : IEventPublisher
    {
        public Task PublishAsync(EventEnvelope envelope, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("forced publish failure for the outbox publish-failure metrics test");
    }

    private OutboxDispatcher CreateDispatcher(RabbitMqConnection connection, int batchSize = 20) =>
        new(fixture.DataSource, new RabbitMqEventPublisher(connection, NullLogger<RabbitMqEventPublisher>.Instance), new SystemClock(),
            Options.Create(new OutboxDispatcherOptions { PollInterval = TimeSpan.FromSeconds(2), BatchSize = batchSize }),
            NullLogger<OutboxDispatcher>.Instance);

    private async Task<IReadOnlyList<Guid>> SeedOutboxRowsAsync(int count)
    {
        await using var db = fixture.CreateDbContext();
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            db.Add(new Ago.Platform.Persistence.Postgres.OutboxMessage(
                id, DateTimeOffset.UtcNow, "MessageAccepted", 1, "{}", $"key-{id:N}", Guid.NewGuid()));
        }

        await db.SaveChangesAsync(CancellationToken.None);
        return ids;
    }

    private async Task<bool> AllPublishedAsync(IReadOnlyList<Guid> ids)
    {
        await using var db = fixture.CreateDbContext();
        var publishedCount = await db.Set<Ago.Platform.Persistence.Postgres.OutboxMessage>()
            .CountAsync(o => ids.Contains(o.Id) && o.PublishedAt != null, CancellationToken.None);
        return publishedCount == ids.Count;
    }

    private async Task<bool> IsPublishedAsync(Guid id)
    {
        await using var db = fixture.CreateDbContext();
        var row = await db.Set<Ago.Platform.Persistence.Postgres.OutboxMessage>().SingleAsync(o => o.Id == id, CancellationToken.None);
        return row.PublishedAt is not null;
    }
}
