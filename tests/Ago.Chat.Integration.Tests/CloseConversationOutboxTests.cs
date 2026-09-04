using Ago.Chat.Application.UseCases.CloseConversation;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ago.Chat.Integration.Tests;

/// <summary>adr/0005, `6-02`: closing a conversation and staging its outbox row happen through the
/// exact same DbContext instance the real handler and real repository would share within one DI scope
/// - the same "same transaction" bar SendMessageOutboxTests already set for messages, now proven for
/// `Conversation.Close()`'s first real caller.</summary>
[Collection(PostgresCollection.Name)]
public class CloseConversationOutboxTests(PostgresFixture fixture)
{
    // Real time, not a fixed date - MessageUniqueSequenceTests' own remarks explain why: the
    // partitioned messages table only ever has partitions for the current month and the next two.
    // Conversations are not partitioned, but every fixture in this file follows the same convention
    // for consistency. Truncated to whole seconds so it round-trips through timestamptz unchanged.
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    [Fact]
    public async Task CloseConversation_WritesOneClosedConversationRowAndOneMatchingOutboxRow()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        var roleId = Guid.NewGuid();

        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            seed.Visitors.Add(new Visitor(visitorId, siteId, Now));
            seed.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
            seed.Roles.Add(new RoleRecord
            {
                Id = roleId,
                SiteId = siteId,
                Name = "Operator",
                Permissions = [Permission.ConversationClose.Value],
            });
            seed.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });

            var conversation = Conversation.Start(conversationId, siteId, visitorId, Now);
            conversation.AssignTo(operatorId, Now);
            conversation.ClearDomainEvents();
            seed.Conversations.Add(conversation);

            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var handler = new CloseConversationHandler(
                new ConversationRepository(db),
                new ConversationAssignmentLog(db),
                new PermissionChecker(db),
                new OperatorCapacityStore(db),
                new EfOutboxWriter<AgoChatDbContext>(db),
                new UuidV7Generator(),
                new SystemClock(),
                NullLogger<CloseConversationHandler>.Instance);

            var result = await handler.HandleAsync(
                new Application.UseCases.CloseConversation.CloseConversation(conversationId, operatorId, siteId),
                CancellationToken.None);

            Assert.True(result.IsSuccess);
        }

        await using var verify = fixture.CreateDbContext();
        var conversationRow = await verify.Conversations.SingleAsync(c => c.Id == conversationId, CancellationToken.None);
        Assert.Equal(ConversationState.Closed, conversationRow.State);

        // Filtered by this conversation's own id (the mapper's MessageId, ConversationClosedMapper's
        // own remarks) rather than a bare Set<OutboxMessage>().SingleAsync() - PostgresFixture is one
        // shared container per collection with no truncation between tests (its own doc comment), so
        // an unfiltered query would collide with SendMessageOutboxTests' row once both run in the same
        // suite.
        var outboxRow = await verify.Set<OutboxMessage>().SingleAsync(o => o.Id == conversationId.Value, CancellationToken.None);
        Assert.Equal(nameof(ConversationEnded), outboxRow.Type);
        Assert.Equal(conversationId.Value.ToString(), outboxRow.PartitionKey);
        Assert.Null(outboxRow.PublishedAt);
    }

    [Fact]
    public async Task CloseConversation_WhenTheOperatorIsNotAssignedToIt_WritesNeitherRow()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var assignedOperatorId = new OperatorId(Guid.NewGuid());
        var otherOperatorId = new OperatorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        var roleId = Guid.NewGuid();

        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            seed.Visitors.Add(new Visitor(visitorId, siteId, Now));
            seed.Operators.Add(new Operator(assignedOperatorId, siteId, OperatorStatus.Online, capacity: 5));
            seed.Operators.Add(new Operator(otherOperatorId, siteId, OperatorStatus.Online, capacity: 5));
            seed.Roles.Add(new RoleRecord
            {
                Id = roleId,
                SiteId = siteId,
                Name = "Operator",
                Permissions = [Permission.ConversationClose.Value],
            });
            seed.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = assignedOperatorId, RoleId = roleId });
            seed.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = otherOperatorId, RoleId = roleId });

            var conversation = Conversation.Start(conversationId, siteId, visitorId, Now);
            conversation.AssignTo(assignedOperatorId, Now);
            conversation.ClearDomainEvents();
            seed.Conversations.Add(conversation);

            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var handler = new CloseConversationHandler(
                new ConversationRepository(db),
                new ConversationAssignmentLog(db),
                new PermissionChecker(db),
                new OperatorCapacityStore(db),
                new EfOutboxWriter<AgoChatDbContext>(db),
                new UuidV7Generator(),
                new SystemClock(),
                NullLogger<CloseConversationHandler>.Instance);

            var result = await handler.HandleAsync(
                new Application.UseCases.CloseConversation.CloseConversation(conversationId, otherOperatorId, siteId),
                CancellationToken.None);

            Assert.True(result.IsFailure);
            Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        }

        await using var verify = fixture.CreateDbContext();
        var conversationRow = await verify.Conversations.SingleAsync(c => c.Id == conversationId, CancellationToken.None);
        Assert.Equal(ConversationState.Assigned, conversationRow.State);
        // Same "filter by this conversation's own id, never the whole shared table" reasoning as the
        // happy-path test above.
        Assert.False(await verify.Set<OutboxMessage>().AnyAsync(o => o.Id == conversationId.Value, CancellationToken.None));
    }

    [Fact]
    public async Task CloseConversation_WithoutTheDedicatedPermission_ReturnsForbidden()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        var roleId = Guid.NewGuid();

        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            seed.Visitors.Add(new Visitor(visitorId, siteId, Now));
            seed.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
            // Grants conversation:assign, not conversation:close - proves the two are independent,
            // adr/0016's own reason for CloseConversation checking a dedicated permission rather than
            // reusing conversation:assign.
            seed.Roles.Add(new RoleRecord
            {
                Id = roleId,
                SiteId = siteId,
                Name = "Operator",
                Permissions = [Permission.ConversationAssign.Value],
            });
            seed.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });

            var conversation = Conversation.Start(conversationId, siteId, visitorId, Now);
            conversation.AssignTo(operatorId, Now);
            conversation.ClearDomainEvents();
            seed.Conversations.Add(conversation);

            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using var db = fixture.CreateDbContext();
        var handler = new CloseConversationHandler(
            new ConversationRepository(db),
            new ConversationAssignmentLog(db),
            new PermissionChecker(db),
            new OperatorCapacityStore(db),
            new EfOutboxWriter<AgoChatDbContext>(db),
            new UuidV7Generator(),
            new SystemClock(),
            NullLogger<CloseConversationHandler>.Instance);

        var result = await handler.HandleAsync(
            new Application.UseCases.CloseConversation.CloseConversation(conversationId, operatorId, siteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }
}
