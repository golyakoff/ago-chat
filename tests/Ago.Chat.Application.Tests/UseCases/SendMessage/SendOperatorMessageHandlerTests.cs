using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.SendMessage;

public class SendOperatorMessageHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static (
        SendOperatorMessageHandler Handler,
        FakeConversationRepository Conversations,
        FakePermissionChecker Permissions,
        Conversation Conversation,
        FakeOutboxWriter Outbox)
        CreateHandlerWithAssignedConversation(bool grantPermission = true)
    {
        var conversations = new FakeConversationRepository();
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversation.AssignTo(OperatorId, Now);
        conversations.Seed(conversation);

        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ConversationSend);
        }

        var outbox = new FakeOutboxWriter();
        var handler = new SendOperatorMessageHandler(
            conversations, permissions, new FakeClock(Now), new FakeIdGenerator(), outbox);
        return (handler, conversations, permissions, conversation, outbox);
    }

    [Fact]
    public async Task HandleAsync_WhenTheOperatorIsAssignedAndPermitted_Succeeds()
    {
        var (handler, _, _, conversation, _) = CreateHandlerWithAssignedConversation();

        var result = await handler.HandleAsync(
            new SendOperatorMessage(conversation.Id, OperatorId, SiteId, "how can I help?"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value);
    }

    [Fact]
    public async Task HandleAsync_WhenTheOperatorIsAssignedAndPermitted_EnqueuesMessageAccepted()
    {
        var (handler, _, _, conversation, outbox) = CreateHandlerWithAssignedConversation();

        await handler.HandleAsync(
            new SendOperatorMessage(conversation.Id, OperatorId, SiteId, "how can I help?"), CancellationToken.None);

        var envelope = Assert.Single(outbox.Enqueued);
        Assert.Equal("MessageAccepted", envelope.Type);
        Assert.Equal(conversation.Id.Value.ToString(), envelope.PartitionKey);
    }

    [Fact]
    public async Task HandleAsync_WhenTheOperatorLacksThePermission_ReturnsForbidden_BeforeTouchingTheConversation()
    {
        var (handler, _, _, conversation, _) = CreateHandlerWithAssignedConversation(grantPermission: false);

        var result = await handler.HandleAsync(
            new SendOperatorMessage(conversation.Id, OperatorId, SiteId, "hi"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Empty(conversation.Messages);
    }

    [Fact]
    public async Task HandleAsync_WhenPermittedButNotTheAssignedOperator_ReturnsForbidden()
    {
        var (handler, _, permissions, conversation, _) = CreateHandlerWithAssignedConversation();
        var someoneElse = new OperatorId(Guid.NewGuid());
        permissions.Grant(someoneElse, SiteId, Permission.ConversationSend);

        var result = await handler.HandleAsync(
            new SendOperatorMessage(conversation.Id, someoneElse, SiteId, "hi"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenConversationIsStillWaiting_ReturnsInvalidState()
    {
        var conversations = new FakeConversationRepository();
        var waiting = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversations.Seed(waiting);
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationSend);
        var handler = new SendOperatorMessageHandler(conversations, permissions, new FakeClock(Now), new FakeIdGenerator(), new FakeOutboxWriter());

        var result = await handler.HandleAsync(
            new SendOperatorMessage(waiting.Id, OperatorId, SiteId, "hi"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.InvalidState", result.Error!.Value.Code);
    }
}
