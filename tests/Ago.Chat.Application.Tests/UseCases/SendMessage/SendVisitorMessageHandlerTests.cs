using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.SendMessage;

public class SendVisitorMessageHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static (SendVisitorMessageHandler Handler, FakeConversationRepository Conversations, Conversation Conversation, FakeOutboxWriter Outbox)
        CreateHandlerWithWaitingConversation()
    {
        var conversations = new FakeConversationRepository();
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversations.Seed(conversation);
        var outbox = new FakeOutboxWriter();
        var handler = new SendVisitorMessageHandler(
            conversations, new FakeClock(Now), new FakeIdGenerator(), outbox, new FakeRateLimiter(), new MessageSendRateLimitOptions());
        return (handler, conversations, conversation, outbox);
    }

    [Fact]
    public async Task HandleAsync_WhenTheVisitorOwnsTheConversation_Succeeds()
    {
        var (handler, _, conversation, _) = CreateHandlerWithWaitingConversation();

        var result = await handler.HandleAsync(
            new SendVisitorMessage(conversation.Id, VisitorId, "hello"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value);
    }

    [Fact]
    public async Task HandleAsync_WhenTheVisitorOwnsTheConversation_EnqueuesMessageAccepted()
    {
        var (handler, _, conversation, outbox) = CreateHandlerWithWaitingConversation();

        await handler.HandleAsync(new SendVisitorMessage(conversation.Id, VisitorId, "hello"), CancellationToken.None);

        var envelope = Assert.Single(outbox.Enqueued);
        Assert.Equal("MessageAccepted", envelope.Type);
        Assert.Equal(conversation.Id.Value.ToString(), envelope.PartitionKey);
    }

    [Fact]
    public async Task HandleAsync_WhenConversationDoesNotExist_ReturnsNotFound()
    {
        var conversations = new FakeConversationRepository();
        var handler = new SendVisitorMessageHandler(
            conversations, new FakeClock(Now), new FakeIdGenerator(), new FakeOutboxWriter(),
            new FakeRateLimiter(), new MessageSendRateLimitOptions());

        var result = await handler.HandleAsync(
            new SendVisitorMessage(new ConversationId(Guid.NewGuid()), VisitorId, "hello"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenThePerVisitorRateLimitIsExceeded_ReturnsRateLimited_BeforeTouchingTheConversation()
    {
        var conversations = new FakeConversationRepository();
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversations.Seed(conversation);
        var limiter = new RateLimitedFakeRateLimiter(TimeSpan.FromSeconds(5));
        var handler = new SendVisitorMessageHandler(
            conversations, new FakeClock(Now), new FakeIdGenerator(), new FakeOutboxWriter(), limiter, new MessageSendRateLimitOptions());

        var result = await handler.HandleAsync(
            new SendVisitorMessage(conversation.Id, VisitorId, "hello"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Message.RateLimited", result.Error!.Value.Code);
        Assert.Contains("5.0s", result.Error!.Value.Message);
        Assert.Empty(conversation.Messages); // denied before ever touching the conversation
    }

    [Fact]
    public async Task HandleAsync_WhenTheAuthorIsNotThisConversationsVisitor_ReturnsForbidden()
    {
        var (handler, _, conversation, _) = CreateHandlerWithWaitingConversation();
        var someoneElse = new VisitorId(Guid.NewGuid());

        var result = await handler.HandleAsync(
            new SendVisitorMessage(conversation.Id, someoneElse, "hello"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenTheBodyIsEmpty_ReturnsInvalidBody()
    {
        var (handler, _, conversation, _) = CreateHandlerWithWaitingConversation();

        var result = await handler.HandleAsync(
            new SendVisitorMessage(conversation.Id, VisitorId, "   "), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Message.InvalidBody", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenTheConversationIsClosed_ReturnsInvalidState()
    {
        var (handler, conversations, conversation, _) = CreateHandlerWithWaitingConversation();
        conversation.Close(Now);
        await conversations.SaveAsync(conversation, CancellationToken.None);

        var result = await handler.HandleAsync(
            new SendVisitorMessage(conversation.Id, VisitorId, "hello"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.InvalidState", result.Error!.Value.Code);
    }
}
