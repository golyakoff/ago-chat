using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.SendMessage;

public class SendVisitorMessageHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static (SendVisitorMessageHandler Handler, FakeConversationRepository Conversations, Conversation Conversation)
        CreateHandlerWithWaitingConversation()
    {
        var conversations = new FakeConversationRepository();
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversations.Seed(conversation);
        var handler = new SendVisitorMessageHandler(conversations, new FakeClock(Now), new FakeIdGenerator());
        return (handler, conversations, conversation);
    }

    [Fact]
    public async Task HandleAsync_WhenTheVisitorOwnsTheConversation_Succeeds()
    {
        var (handler, _, conversation) = CreateHandlerWithWaitingConversation();

        var result = await handler.HandleAsync(
            new SendVisitorMessage(conversation.Id, VisitorId, "hello"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value);
    }

    [Fact]
    public async Task HandleAsync_WhenConversationDoesNotExist_ReturnsNotFound()
    {
        var conversations = new FakeConversationRepository();
        var handler = new SendVisitorMessageHandler(conversations, new FakeClock(Now), new FakeIdGenerator());

        var result = await handler.HandleAsync(
            new SendVisitorMessage(new ConversationId(Guid.NewGuid()), VisitorId, "hello"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenTheAuthorIsNotThisConversationsVisitor_ReturnsForbidden()
    {
        var (handler, _, conversation) = CreateHandlerWithWaitingConversation();
        var someoneElse = new VisitorId(Guid.NewGuid());

        var result = await handler.HandleAsync(
            new SendVisitorMessage(conversation.Id, someoneElse, "hello"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenTheBodyIsEmpty_ReturnsInvalidBody()
    {
        var (handler, _, conversation) = CreateHandlerWithWaitingConversation();

        var result = await handler.HandleAsync(
            new SendVisitorMessage(conversation.Id, VisitorId, "   "), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Message.InvalidBody", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenTheConversationIsClosed_ReturnsInvalidState()
    {
        var (handler, conversations, conversation) = CreateHandlerWithWaitingConversation();
        conversation.Close(Now);
        await conversations.SaveAsync(conversation, CancellationToken.None);

        var result = await handler.HandleAsync(
            new SendVisitorMessage(conversation.Id, VisitorId, "hello"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.InvalidState", result.Error!.Value.Code);
    }
}
