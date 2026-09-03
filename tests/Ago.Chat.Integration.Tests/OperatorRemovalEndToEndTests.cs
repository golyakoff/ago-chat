using Ago.Chat.Application.UseCases.RemoveOperator;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Worker;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Messaging.RabbitMq;
using Ago.Platform.Persistence.Postgres;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `13-03`'s own Done-when: removing an operator releases their `Assigned` conversations back to
/// `Waiting` - the real chain, live: <see cref="RemoveOperatorHandler"/> (stages `OperatorRemovedFromSite`
/// in the outbox, same transaction as `Operator.RemovedAt`) -&gt; real Postgres -&gt;
/// <see cref="OutboxDispatcher"/> -&gt; real RabbitMQ -&gt; <see cref="OperatorRemovedConsumer"/> -&gt;
/// <see cref="OperatorConversationReleaser"/> - the same "real Postgres, real RabbitMQ, hand-wired
/// pipeline stages" shape <c>WidgetConfigCacheInvalidationEndToEndTests</c> already established for
/// `SiteSettingsChanged`'s own chain.
/// </summary>
[Collection(ConnectionFanoutCollection.Name)]
public sealed class OperatorRemovalEndToEndTests(ConnectionFanoutFixture fixture)
{
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    [Fact]
    public async Task RemovingAnOperator_ReleasesTheirAssignedConversationBackToWaiting_ThroughTheRealOutboxAndConsumer()
    {
        var (siteId, operatorId, conversationId) = await SeedAssignedConversationAsync();

        await using var dispatcherConnection = fixture.CreateRabbitMqConnection();
        var dispatcher = new OutboxDispatcher(
            fixture.DataSource, new RabbitMqEventPublisher(dispatcherConnection, NullLogger<RabbitMqEventPublisher>.Instance), new SystemClock(),
            Options.Create(new OutboxDispatcherOptions { PollInterval = TimeSpan.FromMilliseconds(500) }), NullLogger<OutboxDispatcher>.Instance);

        await using var consumerConnection = fixture.CreateRabbitMqConnection();
        var releaser = new OperatorConversationReleaser(fixture.DataSource, new SystemClock(), new UuidV7Generator());
        var consumer = new OperatorRemovedConsumer(
            new RabbitMqEventConsumer(consumerConnection), releaser,
            Options.Create(new OperatorRemovedConsumerOptions()), NullLogger<OperatorRemovedConsumer>.Instance);

        await dispatcher.StartAsync(CancellationToken.None);
        await consumer.StartAsync(CancellationToken.None);
        await consumer.ExecuteTask!; // ready once SubscribeAsync's declare/bind/consume chain lands - OperatorDisconnectGraceEndToEndTests' own precedent.

        try
        {
            await using (var db = fixture.CreateDbContext())
            {
                var operators = new OperatorRepository(db);
                var permissions = new PermissionChecker(db);
                var outbox = new EfOutboxWriter<AgoChatDbContext>(db);
                var handler = new RemoveOperatorHandler(operators, permissions, outbox, new UuidV7Generator(), new SystemClock());

                var adminOperatorId = await SeedAdminAsync(db, siteId);
                var result = await handler.HandleAsync(new RemoveOperator(adminOperatorId, siteId, operatorId), CancellationToken.None);
                Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Message : null);
            }

            var released = await OutboxTestHelpers.WaitUntilAsync(async () => await IsWaitingAsync(conversationId), TimeSpan.FromSeconds(15));
            Assert.True(released, "Timed out waiting for the removed operator's conversation to be released back to Waiting.");
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
            await consumer.StopAsync(CancellationToken.None);
            consumer.Dispose();
        }

        await using var verify = fixture.CreateDbContext();
        var op = await verify.Operators.FindAsync(operatorId);
        Assert.NotNull(op!.RemovedAt);
        Assert.Equal(0, await ReadActiveChatsAsync(operatorId));
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

    /// <summary>A second operator, admin-permissioned, so <see cref="RemoveOperatorHandler"/>'s own
    /// permission check has a real caller to authorize - never the operator being removed itself.</summary>
    private static async Task<OperatorId> SeedAdminAsync(AgoChatDbContext db, SiteId siteId)
    {
        var adminOperatorId = new OperatorId(Guid.NewGuid());
        var roleId = Guid.NewGuid();
        db.Operators.Add(new Operator(adminOperatorId, siteId, OperatorStatus.Offline, capacity: 5, externalSubjectId: "sub-admin"));
        db.Roles.Add(new RoleRecord { Id = roleId, SiteId = siteId, Name = "Admin", Permissions = [Permission.SiteManageOperators.Value] });
        db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = adminOperatorId, RoleId = roleId });
        await db.SaveChangesAsync();
        return adminOperatorId;
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
