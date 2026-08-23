using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetOperatorQueue;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetOperatorQueue;

public class GetOperatorQueueHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HandleAsync_ReturnsWaitingConversationsForTheSite_AndAssignedConversationsForThisOperator()
    {
        var waiting = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        var assignedToMe = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, new VisitorId(Guid.NewGuid()), Now);
        assignedToMe.AssignTo(OperatorId, Now);
        var assignedToSomeoneElse = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, new VisitorId(Guid.NewGuid()), Now);
        assignedToSomeoneElse.AssignTo(new OperatorId(Guid.NewGuid()), Now);

        var (handler, conversations) = CreateHandler();
        conversations.Seed(waiting);
        conversations.Seed(assignedToMe);
        conversations.Seed(assignedToSomeoneElse);

        var result = await handler.HandleAsync(new Application.UseCases.GetOperatorQueue.GetOperatorQueue(OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var single = Assert.Single(result.Value.Waiting);
        Assert.Equal(waiting.Id.Value, single.ConversationId);
        var assigned = Assert.Single(result.Value.AssignedToMe);
        Assert.Equal(assignedToMe.Id.Value, assigned.ConversationId);
    }

    [Fact]
    public async Task HandleAsync_WaitingConversationFromAnotherSite_IsExcluded()
    {
        var otherSite = new SiteId(Guid.NewGuid());
        var waitingElsewhere = Conversation.Start(new ConversationId(Guid.NewGuid()), otherSite, VisitorId, Now);

        var (handler, conversations) = CreateHandler();
        conversations.Seed(waitingElsewhere);

        var result = await handler.HandleAsync(new Application.UseCases.GetOperatorQueue.GetOperatorQueue(OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Waiting);
    }

    [Fact]
    public async Task HandleAsync_OperatorWithoutConversationReadPermission_ReturnsForbidden()
    {
        var conversations = new FakeConversationRepository();
        var permissions = new FakePermissionChecker();
        var handler = new GetOperatorQueueHandler(conversations, permissions);

        var result = await handler.HandleAsync(new Application.UseCases.GetOperatorQueue.GetOperatorQueue(OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    private static (GetOperatorQueueHandler Handler, FakeConversationRepository Conversations) CreateHandler()
    {
        var conversations = new FakeConversationRepository();
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationRead);
        return (new GetOperatorQueueHandler(conversations, permissions), conversations);
    }
}
