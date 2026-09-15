using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.RecordUnread;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.RecordUnread;

public class RecordUnreadMessageHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        RecordUnreadMessageHandler Handler, FakeConversationRepository Conversations,
        FakeUnreadCounterStore UnreadCounter, FakeUnitOfWork UnitOfWork, Conversation Conversation);

    private static Fixture CreateHandlerWithConversation()
    {
        var conversations = new FakeConversationRepository();
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversations.Seed(conversation);
        var unreadCounter = new FakeUnreadCounterStore();
        var unitOfWork = new FakeUnitOfWork();
        var handler = new RecordUnreadMessageHandler(conversations, unreadCounter, unitOfWork, new FakeInboxChecker());
        return new Fixture(handler, conversations, unreadCounter, unitOfWork, conversation);
    }

    [Fact]
    public async Task HandleAsync_VisitorAuthoredMessage_AsksTheStoreToIncrementTheOperatorsCount()
    {
        var fixture = CreateHandlerWithConversation();

        var result = await fixture.Handler.HandleAsync(
            new RecordUnreadMessage(Guid.NewGuid(), fixture.Conversation.Id, MessageAuthorKind.Visitor, Sequence: 1),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var call = Assert.Single(fixture.UnreadCounter.Calls);
        Assert.Equal(fixture.Conversation.Id, call.ConversationId);
        Assert.Equal(MessageAuthorKind.Visitor, call.AuthorKind);
        Assert.Equal(1, call.Sequence);
    }

    [Fact]
    public async Task HandleAsync_OperatorAuthoredMessage_AsksTheStoreToIncrementTheVisitorsCount()
    {
        var fixture = CreateHandlerWithConversation();

        await fixture.Handler.HandleAsync(
            new RecordUnreadMessage(Guid.NewGuid(), fixture.Conversation.Id, MessageAuthorKind.Operator, Sequence: 1),
            CancellationToken.None);

        var call = Assert.Single(fixture.UnreadCounter.Calls);
        Assert.Equal(MessageAuthorKind.Operator, call.AuthorKind);
    }

    [Fact]
    public async Task HandleAsync_WhenConversationDoesNotExist_ReturnsNotFound()
    {
        var conversations = new FakeConversationRepository();
        var handler = new RecordUnreadMessageHandler(
            conversations, new FakeUnreadCounterStore(), new FakeUnitOfWork(), new FakeInboxChecker());

        var result = await handler.HandleAsync(
            new RecordUnreadMessage(Guid.NewGuid(), new ConversationId(Guid.NewGuid()), MessageAuthorKind.Visitor, Sequence: 1),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenConversationDoesNotExist_NeverOpensATransaction()
    {
        // No transaction to commit or roll back when there is nothing to increment and no inbox row
        // to stage - the existence check short-circuits before IUnitOfWork.BeginTransactionAsync.
        var conversations = new FakeConversationRepository();
        var unitOfWork = new FakeUnitOfWork();
        var handler = new RecordUnreadMessageHandler(
            conversations, new FakeUnreadCounterStore(), unitOfWork, new FakeInboxChecker());

        await handler.HandleAsync(
            new RecordUnreadMessage(Guid.NewGuid(), new ConversationId(Guid.NewGuid()), MessageAuthorKind.Visitor, Sequence: 1),
            CancellationToken.None);

        Assert.Equal(0, unitOfWork.TransactionsBegun);
    }

    [Fact]
    public async Task HandleAsync_FirstDelivery_ReturnsTrueAndCommitsTheTransaction()
    {
        var fixture = CreateHandlerWithConversation();

        var result = await fixture.Handler.HandleAsync(
            new RecordUnreadMessage(Guid.NewGuid(), fixture.Conversation.Id, MessageAuthorKind.Visitor, Sequence: 1),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value);
        Assert.Equal(1, fixture.UnitOfWork.TransactionsBegun);
        Assert.Equal(1, fixture.UnitOfWork.TransactionsCommitted);
    }

    [Fact]
    public async Task HandleAsync_SameMessageIdTwice_SecondCallReturnsFalseAndDoesNotCommit()
    {
        // What this proves: the handler asks IInboxChecker the right (messageId, consumer) question,
        // returns its verdict, and only commits the transaction that carries the raw-SQL increment
        // when the inbox row was actually new. What it does NOT prove: that a real duplicate leaves
        // the counter genuinely untouched in Postgres - FakeUnitOfWork tracks the commit decision, not
        // a real rollback (its own remarks). That guarantee is Ago.Chat.Integration.Tests' job,
        // against real Postgres, exactly as FakeInboxChecker's own remarks already say for the inbox
        // half of this same commit.
        var fixture = CreateHandlerWithConversation();
        var command = new RecordUnreadMessage(Guid.NewGuid(), fixture.Conversation.Id, MessageAuthorKind.Visitor, Sequence: 1);

        var first = await fixture.Handler.HandleAsync(command, CancellationToken.None);
        var second = await fixture.Handler.HandleAsync(command, CancellationToken.None);

        Assert.True(first.Value);
        Assert.False(second.Value);
        Assert.Equal(2, fixture.UnitOfWork.TransactionsBegun);
        Assert.Equal(1, fixture.UnitOfWork.TransactionsCommitted);
    }
}
