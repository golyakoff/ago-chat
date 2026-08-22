using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetConversationHistory;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetConversationHistory;

public class GetConversationHistoryHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static (
        GetConversationHistoryHandler Handler, FakePermissionChecker Permissions, Conversation Conversation)
        CreateHandlerWithHistory()
    {
        var conversations = new FakeConversationRepository();
        var readStore = new FakeConversationReadStore();
        var permissions = new FakePermissionChecker();

        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversation.AssignTo(OperatorId, Now);
        conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        conversation.AddOperatorMessage(OperatorId, new MessageId(Guid.NewGuid()), new MessageBody("hello"), Now);

        conversations.Seed(conversation);
        readStore.Seed(conversation);
        permissions.Grant(OperatorId, SiteId, Permission.ConversationRead);

        var handler = new GetConversationHistoryHandler(conversations, readStore, permissions);
        return (handler, permissions, conversation);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_WhenTheVisitorOwnsTheConversation_ReturnsTheHistory()
    {
        var (handler, _, conversation) = CreateHandlerWithHistory();

        var result = await handler.HandleAsVisitorAsync(
            new GetConversationHistoryAsVisitor(conversation.Id, VisitorId, BeforeSequence: null, PageSize: 10),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Messages.Count);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_WhenTheRequesterIsNotThisConversationsVisitor_ReturnsForbidden()
    {
        var (handler, _, conversation) = CreateHandlerWithHistory();
        var someoneElse = new VisitorId(Guid.NewGuid());

        var result = await handler.HandleAsVisitorAsync(
            new GetConversationHistoryAsVisitor(conversation.Id, someoneElse, BeforeSequence: null, PageSize: 10),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsOperatorAsync_WhenAssignedAndPermitted_ReturnsTheHistory()
    {
        var (handler, _, conversation) = CreateHandlerWithHistory();

        var result = await handler.HandleAsOperatorAsync(
            new GetConversationHistoryAsOperator(conversation.Id, OperatorId, SiteId, BeforeSequence: null, PageSize: 10),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Messages.Count);
    }

    [Fact]
    public async Task HandleAsOperatorAsync_WhenNotPermitted_ReturnsForbidden()
    {
        var (handler, _, conversation) = CreateHandlerWithHistory();
        var unpermitted = new OperatorId(Guid.NewGuid());

        var result = await handler.HandleAsOperatorAsync(
            new GetConversationHistoryAsOperator(conversation.Id, unpermitted, SiteId, BeforeSequence: null, PageSize: 10),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsOperatorAsync_WhenPermittedButNotAssignedToThisConversation_ReturnsForbidden()
    {
        var (handler, permissions, conversation) = CreateHandlerWithHistory();
        var someoneElse = new OperatorId(Guid.NewGuid());
        permissions.Grant(someoneElse, SiteId, Permission.ConversationRead);

        var result = await handler.HandleAsOperatorAsync(
            new GetConversationHistoryAsOperator(conversation.Id, someoneElse, SiteId, BeforeSequence: null, PageSize: 10),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_WhenConversationDoesNotExist_ReturnsNotFound()
    {
        var conversations = new FakeConversationRepository();
        var readStore = new FakeConversationReadStore();
        var handler = new GetConversationHistoryHandler(conversations, readStore, new FakePermissionChecker());

        var result = await handler.HandleAsVisitorAsync(
            new GetConversationHistoryAsVisitor(new ConversationId(Guid.NewGuid()), VisitorId, null, 10),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleDeltaAsVisitorAsync_ReturnsOnlyMessagesAfterTheGivenSequence_OldestFirst()
    {
        var (handler, _, conversation) = CreateHandlerWithHistory();

        var result = await handler.HandleDeltaAsVisitorAsync(
            new GetConversationDeltaAsVisitor(conversation.Id, VisitorId, AfterSequence: 1), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value);
        Assert.Equal(2, item.Sequence);
    }

    [Fact]
    public async Task HandleDeltaAsVisitorAsync_WhenTheRequesterIsNotThisConversationsVisitor_ReturnsForbidden()
    {
        var (handler, _, conversation) = CreateHandlerWithHistory();
        var someoneElse = new VisitorId(Guid.NewGuid());

        var result = await handler.HandleDeltaAsVisitorAsync(
            new GetConversationDeltaAsVisitor(conversation.Id, someoneElse, AfterSequence: 0), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleDeltaAsOperatorAsync_ReturnsOnlyMessagesAfterTheGivenSequence_OldestFirst()
    {
        var (handler, _, conversation) = CreateHandlerWithHistory();

        var result = await handler.HandleDeltaAsOperatorAsync(
            new GetConversationDeltaAsOperator(conversation.Id, OperatorId, SiteId, AfterSequence: 0), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal([1, 2], result.Value.Select(m => m.Sequence));
    }

    [Fact]
    public async Task HandleDeltaAsOperatorAsync_WhenNotAssignedToThisConversation_ReturnsForbidden()
    {
        var (handler, permissions, conversation) = CreateHandlerWithHistory();
        var someoneElse = new OperatorId(Guid.NewGuid());
        permissions.Grant(someoneElse, SiteId, Permission.ConversationRead);

        var result = await handler.HandleDeltaAsOperatorAsync(
            new GetConversationDeltaAsOperator(conversation.Id, someoneElse, SiteId, AfterSequence: 0), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }
}
