using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Worker;
using Ago.Platform.Kernel;
using Ago.Platform.Messaging.RabbitMq;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Concurrency.Tests;

/// <summary>testing.md's "Idempotency" concurrency test: deliver the same event twice, assert one
/// row and one delivery.</summary>
[Collection(ConcurrencyCollection.Name)]
public sealed class UnreadCounterIdempotencyTests(ConcurrencyTestFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DeliveringTheSameMessageAcceptedTwice_IncrementsExactlyOnceAndRecordsOneInboxRow()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        var messageId = Guid.NewGuid();

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Visitors.Add(new Visitor(visitorId, siteId, Now));
            db.Add(Conversation.Start(conversationId, siteId, visitorId, Now));
            await db.SaveChangesAsync(CancellationToken.None);
        }

        // Same MessageId published twice, on purpose - not a redelivery the broker itself triggers,
        // but the exact effect one would have: two independent deliveries of one logical event.
        var domainEvent = new MessageAdded(
            new MessageId(messageId), conversationId, siteId, Sequence: 1, MessageAuthorKind.Visitor, Now);
        var envelope = MessageAcceptedMapper.ToEnvelope(domainEvent, new UuidV7Generator());

        var rabbitOptions = Options.Create(fixture.BuildRabbitMqOptions());

        await using var services = fixture.CreateServiceProvider();
        await using var consumerConnection = new RabbitMqConnection(rabbitOptions);
        var consumer = new UnreadCounterConsumer(
            new RabbitMqEventConsumer(consumerConnection),
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new UnreadCounterConsumerOptions()),
            NullLogger<UnreadCounterConsumer>.Instance);

        // Start the consumer before publishing anything: a fanout exchange drops a message
        // published before any queue is bound to it (Competing mode's durable queue is only
        // declared once SubscribeAsync runs) - publishing first would silently lose it, not defer
        // it, unlike a real deployment where Worker replicas are already subscribed before traffic
        // starts flowing. A fixed delay here (this test's original approach) is not a real
        // readiness signal - RabbitMqEventConsumer.SubscribeAsync's declare/bind/consume chain is
        // several broker round trips, and BackgroundService.StartAsync returns as soon as
        // ExecuteAsync is scheduled, not once it has run (the same class of race 3-06's
        // ConnectionDrainCoordinator hit). Found live: this exact test dropped every message on a
        // slower CI runner where 500ms was not enough. UnreadCounterConsumer.ExecuteAsync is
        // literally `return consumer.SubscribeAsync(...)`, and SubscribeAsync's own Task completes
        // right after BasicConsumeAsync registers the handler (event-driven delivery afterward does
        // not block it) - so awaiting ExecuteTask is the actual "queue exists and is bound" signal,
        // not a guess at how long that might take.
        await consumer.StartAsync(CancellationToken.None);
        await consumer.ExecuteTask!;
        try
        {
            await using var publisherConnection = new RabbitMqConnection(rabbitOptions);
            var publisher = new RabbitMqEventPublisher(publisherConnection);
            await publisher.PublishAsync(envelope, CancellationToken.None);
            await publisher.PublishAsync(envelope, CancellationToken.None);

            await ConcurrencyTestHelpers.WaitUntilAsync(
                async () =>
                {
                    await using var db = fixture.CreateDbContext();
                    var conversation = await db.Conversations.FirstAsync(c => c.Id == conversationId, CancellationToken.None);
                    return conversation.OperatorUnreadCount >= 1;
                },
                TimeSpan.FromSeconds(15));

            // Give a genuinely duplicate delivery time to land too, if it were going to double-count -
            // the wait above only proves the first delivery worked.
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await consumer.StopAsync(CancellationToken.None);
        }

        await using var verify = fixture.CreateDbContext();
        var reloaded = await verify.Conversations.FirstAsync(c => c.Id == conversationId, CancellationToken.None);
        Assert.Equal(1, reloaded.OperatorUnreadCount);
        Assert.Equal(0, reloaded.VisitorUnreadCount);

        var inboxRowCount = await verify.Set<InboxRecord>()
            .CountAsync(r => r.MessageId == messageId, CancellationToken.None);
        Assert.Equal(1, inboxRowCount);
    }
}
