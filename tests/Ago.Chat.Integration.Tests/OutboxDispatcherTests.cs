using System.Collections.Concurrent;
using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Domain;
using Ago.Chat.Worker;
using Ago.Platform.Abstractions;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Messaging.RabbitMq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
        var dispatcher = new OutboxDispatcher(fixture.DataSource, new RabbitMqEventPublisher(connection), new SystemClock(), options, NullLogger<OutboxDispatcher>.Instance);

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

    private OutboxDispatcher CreateDispatcher(RabbitMqConnection connection, int batchSize = 20) =>
        new(fixture.DataSource, new RabbitMqEventPublisher(connection), new SystemClock(),
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
