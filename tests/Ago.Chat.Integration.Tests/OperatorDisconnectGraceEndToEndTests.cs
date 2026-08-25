using Ago.Chat.Application.Realtime;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Module;
using Ago.Chat.Worker;
using Ago.Platform.Abstractions;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Messaging.RabbitMq;
using Ago.Platform.Realtime;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `4-04`'s Done-when, live: `OperatorPresenceLost` -&gt; `OperatorDisconnectGraceConsumer` -&gt;
/// `OperatorConversationReleaser`, against real Postgres, RabbitMQ, and Redis. Uses a short
/// `GracePeriod` so the test itself stays fast, not the production default.
/// </summary>
[Collection(ConnectionFanoutCollection.Name)]
public sealed class OperatorDisconnectGraceEndToEndTests(ConnectionFanoutFixture fixture)
{
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);
    private static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task OperatorWithNoConnections_AfterTheFullGracePeriod_HasTheirConversationReleased()
    {
        await PurgeOperatorPresenceLostQueueAsync();
        var (siteId, operatorId, conversationId) = await SeedAssignedConversationAsync();
        var registry = BuildRegistry();
        // Deliberately no RegisterAsync call - this operator has zero connections from the start,
        // matching "already disconnected by the time the signal is handled."

        var started = await StartConsumerAsync(registry);
        try
        {
            await using var publisherConnection = fixture.CreateRabbitMqConnection();
            var publisher = new OperatorPresencePublisher(new RabbitMqEventPublisher(publisherConnection), new SystemClock(), new UuidV7Generator());
            await publisher.PublishLostAsync(operatorId, siteId, CancellationToken.None);

            var released = await OutboxTestHelpers.WaitUntilAsync(
                async () => await IsWaitingAsync(conversationId), TimeSpan.FromSeconds(15));
            Assert.True(released, "Timed out waiting for the conversation to be released back to Waiting.");
        }
        finally
        {
            await StopConsumerAsync(started);
        }

        await using var verify = fixture.CreateDbContext();
        var conversation = await verify.Conversations.FindAsync(conversationId);
        Assert.Equal(ConversationState.Waiting, conversation!.State);
        Assert.Null(conversation.OperatorId);
        Assert.Equal(0, await ReadActiveChatsAsync(operatorId));
    }

    [Fact]
    public async Task OperatorReconnects_WithinTheGracePeriod_ConversationStaysAssigned()
    {
        await PurgeOperatorPresenceLostQueueAsync();
        var (siteId, operatorId, conversationId) = await SeedAssignedConversationAsync();
        var registry = BuildRegistry();

        var started = await StartConsumerAsync(registry);
        try
        {
            await using var publisherConnection = fixture.CreateRabbitMqConnection();
            var publisher = new OperatorPresencePublisher(new RabbitMqEventPublisher(publisherConnection), new SystemClock(), new UuidV7Generator());
            await publisher.PublishLostAsync(operatorId, siteId, CancellationToken.None);

            // Reconnect partway through the grace period - well before it elapses, well after the
            // signal was published.
            await Task.Delay(TimeSpan.FromMilliseconds(GracePeriod.TotalMilliseconds * 0.3));
            await registry.RegisterAsync(
                new ConnectionId(Guid.NewGuid().ToString()), new NodeId("node-reconnect"),
                PrincipalKeys.ForOperator(operatorId), CancellationToken.None);

            // Wait past the full grace period - the consumer's own final check should now see the
            // reconnected operator and release nothing.
            await Task.Delay(GracePeriod + TimeSpan.FromSeconds(2));
        }
        finally
        {
            await StopConsumerAsync(started);
        }

        await using var verify = fixture.CreateDbContext();
        var conversation = await verify.Conversations.FindAsync(conversationId);
        Assert.Equal(ConversationState.Assigned, conversation!.State);
        Assert.Equal(operatorId, conversation.OperatorId);
        Assert.Equal(1, await ReadActiveChatsAsync(operatorId));
    }

    [Fact]
    public async Task AReleasedConversation_IsVisibleToTheAssignmentEngine_AndGetsReassigned()
    {
        await PurgeOperatorPresenceLostQueueAsync();
        var (siteId, operatorId, conversationId) = await SeedAssignedConversationAsync();
        var registry = BuildRegistry();

        var started = await StartConsumerAsync(registry);
        try
        {
            await using var publisherConnection = fixture.CreateRabbitMqConnection();
            var publisher = new OperatorPresencePublisher(new RabbitMqEventPublisher(publisherConnection), new SystemClock(), new UuidV7Generator());
            await publisher.PublishLostAsync(operatorId, siteId, CancellationToken.None);

            var released = await OutboxTestHelpers.WaitUntilAsync(
                async () => await IsWaitingAsync(conversationId), TimeSpan.FromSeconds(15));
            Assert.True(released, "Timed out waiting for the conversation to be released back to Waiting.");
        }
        finally
        {
            await StopConsumerAsync(started);
        }

        // The same operator is still Status=Online in the database (4-04 does not itself flip
        // status - see the backlog's own Out of scope) and now has room again, so the assignment
        // engine picks the conversation right back up - proving "released" genuinely means
        // "visible to 4-02 again," not just "no longer Assigned."
        var claimer = new SkipLockedAssignmentClaimer(fixture.DataSource, new SystemClock(), new UuidV7Generator());
        var assignedCount = await claimer.AssignWaitingConversationsAsync(siteId, batchSize: 10, CancellationToken.None);
        Assert.Equal(1, assignedCount);

        await using var verify = fixture.CreateDbContext();
        var conversation = await verify.Conversations.FindAsync(conversationId);
        Assert.Equal(ConversationState.Assigned, conversation!.State);
        Assert.Equal(operatorId, conversation.OperatorId);
    }

    private async Task<(SiteId SiteId, OperatorId OperatorId, ConversationId ConversationId)> SeedAssignedConversationAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
            db.Visitors.Add(new Visitor(visitorId, siteId, Now));
            var conversation = Conversation.Start(conversationId, siteId, visitorId, Now);
            // `6-09`: holdsCapacityClaim: true - this seed stands in for an engine-made assignment,
            // which is what the `active_chats = 1` written right below actually represents, and the
            // sweep now hands a slot back only for a conversation that holds the receipt for one.
            conversation.AssignTo(operatorId, Now, holdsCapacityClaim: true);
            db.Conversations.Add(conversation);
            await db.SaveChangesAsync();
        }

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("UPDATE operators SET active_chats = 1 WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", operatorId.Value);
        await command.ExecuteNonQueryAsync();

        return (siteId, operatorId, conversationId);
    }

    /// <summary>All three tests above subscribe to the same Competing-mode topic
    /// (`nameof(OperatorPresenceLost)`) - the one durable queue production hard-codes the name of.
    /// A message left unacked when one test's consumer stops gets requeued by the broker and would
    /// otherwise be redelivered to the *next* test's consumer, delaying or masking that test's own
    /// signal. Purging at the start of each test - after the same queue declare `SubscribeAsync`
    /// itself does, so purging a queue that does not exist yet on the very first test never throws -
    /// keeps the three tests independent despite sharing the one durable queue name.</summary>
    private async Task PurgeOperatorPresenceLostQueueAsync()
    {
        await using var connection = fixture.CreateRabbitMqConnection();
        await using var channel = await connection.CreateChannelAsync();
        await channel.QueueDeclareAsync(nameof(OperatorPresenceLost), durable: true, exclusive: false, autoDelete: false);
        await channel.QueuePurgeAsync(nameof(OperatorPresenceLost));
    }

    private RedisConnectionRegistry BuildRegistry() => new(
        fixture.RedisMultiplexer, Options.Create(new ConnectionRegistryOptions()), NullLogger<RedisConnectionRegistry>.Instance);

    /// <summary>The caller must dispose the returned connection once done (`StopConsumerAsync`) -
    /// a connection left open past its own test leaves its consumer registration alive on the
    /// broker, silently competing for the *next* test's own delivery on the shared Competing queue
    /// (found live: a leaked connection from an earlier test intercepted a later test's publish,
    /// with nothing to show for it - no exception, no log, just a 15s timeout on a delivery that
    /// never reached the consumer actually under test).</summary>
    private async Task<(OperatorDisconnectGraceConsumer Consumer, RabbitMqConnection Connection)> StartConsumerAsync(
        IConnectionRegistry registry)
    {
        var consumerConnection = fixture.CreateRabbitMqConnection();
        var consumer = new OperatorDisconnectGraceConsumer(
            new RabbitMqEventConsumer(consumerConnection), registry,
            new OperatorConversationReleaser(fixture.DataSource, new SystemClock(), new UuidV7Generator()),
            Options.Create(new OperatorDisconnectGraceConsumerOptions { GracePeriod = GracePeriod }),
            NullLogger<OperatorDisconnectGraceConsumer>.Instance);

        await consumer.StartAsync(CancellationToken.None);
        // BackgroundService.StartAsync returns as soon as ExecuteAsync is scheduled, not once the
        // queue is actually declared and bound - see UnreadCounterIdempotencyTests' own remarks on
        // this exact race. ExecuteTask completes right after SubscribeAsync's declare/bind/consume
        // chain does, which is the real readiness signal.
        await consumer.ExecuteTask!;
        return (consumer, consumerConnection);
    }

    private static async Task StopConsumerAsync((OperatorDisconnectGraceConsumer Consumer, RabbitMqConnection Connection) started)
    {
        await started.Consumer.StopAsync(CancellationToken.None);
        started.Consumer.Dispose();
        await started.Connection.DisposeAsync();
    }

    private async Task<bool> IsWaitingAsync(ConversationId conversationId)
    {
        await using var db = fixture.CreateDbContext();
        var conversation = await db.Conversations.FindAsync(conversationId);
        return conversation!.State == ConversationState.Waiting;
    }

    private async Task<int> ReadActiveChatsAsync(OperatorId operatorId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT active_chats FROM operators WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", operatorId.Value);
        return (int)(await command.ExecuteScalarAsync())!;
    }
}
