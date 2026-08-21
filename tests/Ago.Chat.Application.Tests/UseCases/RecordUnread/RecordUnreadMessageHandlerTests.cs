using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.RecordUnread;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.RecordUnread;

public class RecordUnreadMessageHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static (RecordUnreadMessageHandler Handler, FakeConversationRepository Conversations, Conversation Conversation)
        CreateHandlerWithConversation()
    {
        var conversations = new FakeConversationRepository();
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversations.Seed(conversation);
        var handler = new RecordUnreadMessageHandler(conversations, new FakeInboxChecker());
        return (handler, conversations, conversation);
    }

    [Fact]
    public async Task HandleAsync_VisitorAuthoredMessage_IncrementsTheOperatorsUnreadCount()
    {
        var (handler, _, conversation) = CreateHandlerWithConversation();

        var result = await handler.HandleAsync(
            new RecordUnreadMessage(Guid.NewGuid(), conversation.Id, MessageAuthorKind.Visitor),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, conversation.OperatorUnreadCount);
        Assert.Equal(0, conversation.VisitorUnreadCount);
    }

    [Fact]
    public async Task HandleAsync_OperatorAuthoredMessage_IncrementsTheVisitorsUnreadCount()
    {
        var (handler, _, conversation) = CreateHandlerWithConversation();

        await handler.HandleAsync(
            new RecordUnreadMessage(Guid.NewGuid(), conversation.Id, MessageAuthorKind.Operator),
            CancellationToken.None);

        Assert.Equal(1, conversation.VisitorUnreadCount);
        Assert.Equal(0, conversation.OperatorUnreadCount);
    }

    [Fact]
    public async Task HandleAsync_WhenConversationDoesNotExist_ReturnsNotFound()
    {
        var conversations = new FakeConversationRepository();
        var handler = new RecordUnreadMessageHandler(conversations, new FakeInboxChecker());

        var result = await handler.HandleAsync(
            new RecordUnreadMessage(Guid.NewGuid(), new ConversationId(Guid.NewGuid()), MessageAuthorKind.Visitor),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_FirstDelivery_ReturnsTrue()
    {
        var (handler, _, conversation) = CreateHandlerWithConversation();

        var result = await handler.HandleAsync(
            new RecordUnreadMessage(Guid.NewGuid(), conversation.Id, MessageAuthorKind.Visitor),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value);
    }

    [Fact]
    public async Task HandleAsync_SameMessageIdTwice_SecondCallReturnsFalse()
    {
        // What this proves: the handler asks IInboxChecker the right (messageId, consumer)
        // question and returns its verdict. What it does NOT prove: that a real duplicate leaves
        // the counter untouched - FakeInboxChecker has no transaction to roll back the increment
        // this handler already staged, unlike EfInboxChecker against real Postgres (adr/0017). That
        // guarantee is Ago.Chat.Concurrency.Tests' and Ago.Chat.Integration.Tests' job.
        var (handler, _, conversation) = CreateHandlerWithConversation();
        var command = new RecordUnreadMessage(Guid.NewGuid(), conversation.Id, MessageAuthorKind.Visitor);

        var first = await handler.HandleAsync(command, CancellationToken.None);
        var second = await handler.HandleAsync(command, CancellationToken.None);

        Assert.True(first.Value);
        Assert.False(second.Value);
    }
}
