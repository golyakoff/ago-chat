using Ago.Chat.Application.Realtime;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetVisitorPresence;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;

namespace Ago.Chat.Application.Tests.UseCases.GetVisitorPresence;

public class GetVisitorPresenceHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HandleAsync_VisitorHasARegisteredConnection_ReturnsTrue()
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversation.AssignTo(OperatorId, Now);
        var (handler, registry) = CreateHandler(conversation);
        registry.SeedConnected(
            PrincipalKeys.ForVisitor(VisitorId), new RegisteredConnection(new ConnectionId("conn-1"), new NodeId("node-1")));

        var result = await handler.HandleAsync(
            new Application.UseCases.GetVisitorPresence.GetVisitorPresence(conversation.Id, OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value);
    }

    [Fact]
    public async Task HandleAsync_VisitorHasNoRegisteredConnection_ReturnsFalse()
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversation.AssignTo(OperatorId, Now);
        var (handler, _) = CreateHandler(conversation);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetVisitorPresence.GetVisitorPresence(conversation.Id, OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value);
    }

    [Fact]
    public async Task HandleAsync_RequestedByAnOperatorNotAssignedToThisConversation_ReturnsForbidden()
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversation.AssignTo(OperatorId, Now);
        var (handler, _) = CreateHandler(conversation);
        var someoneElse = new OperatorId(Guid.NewGuid());

        var result = await handler.HandleAsync(
            new Application.UseCases.GetVisitorPresence.GetVisitorPresence(conversation.Id, someoneElse, SiteId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_ConversationDoesNotExist_ReturnsNotFound()
    {
        var conversations = new FakeConversationRepository();
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationRead);
        var handler = new GetVisitorPresenceHandler(conversations, permissions, new FakeConnectionRegistry());

        var result = await handler.HandleAsync(
            new Application.UseCases.GetVisitorPresence.GetVisitorPresence(new ConversationId(Guid.NewGuid()), OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    private static (GetVisitorPresenceHandler Handler, FakeConnectionRegistry Registry) CreateHandler(Conversation conversation)
    {
        var conversations = new FakeConversationRepository();
        conversations.Seed(conversation);
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationRead);
        var registry = new FakeConnectionRegistry();
        return (new GetVisitorPresenceHandler(conversations, permissions, registry), registry);
    }
}
