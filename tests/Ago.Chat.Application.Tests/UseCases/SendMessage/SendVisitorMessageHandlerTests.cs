using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases;
using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Tests.UseCases.SendMessage;

/// <summary>
/// `4-05`: the handler's own job shrank to "cheap pre-checks, then hand off to the pipeline" - the
/// actual mutation/outbox/save it used to do inline now happens in
/// `Ago.Chat.Api.Pipeline.MessageBatchWriter`, proven against real Postgres in
/// `Ago.Chat.Integration.Tests.MessageBatchWriterTests`, not here. What is still worth a handler-
/// level test: which checks run *before* enqueueing (and in what order), and that a successful
/// pre-check forwards exactly what the pipeline was told and returns exactly what it answered.
/// </summary>
public class SendVisitorMessageHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static (SendVisitorMessageHandler Handler, FakeConversationRepository Conversations, Conversation Conversation, FakeMessagePipeline Pipeline)
        CreateHandlerWithWaitingConversation(FakeMessagePipeline? pipeline = null)
    {
        var conversations = new FakeConversationRepository();
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversations.Seed(conversation);
        pipeline ??= new FakeMessagePipeline();
        var handler = new SendVisitorMessageHandler(conversations, new FakeRateLimiter(), new MessageSendRateLimitOptions(), pipeline);
        return (handler, conversations, conversation, pipeline);
    }

    [Fact]
    public async Task HandleAsync_WhenTheChecksPass_EnqueuesTheMessageAndReturnsThePipelinesResult()
    {
        var pipeline = new FakeMessagePipeline(42);
        var (handler, _, conversation, _) = CreateHandlerWithWaitingConversation(pipeline);

        var result = await handler.HandleAsync(
            new SendVisitorMessage(conversation.Id, VisitorId, "hello"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
        var pending = Assert.Single(pipeline.Enqueued);
        Assert.Equal(conversation.Id, pending.ConversationId);
        Assert.Equal(MessageAuthorKind.Visitor, pending.AuthorKind);
        Assert.Equal(VisitorId.Value, pending.AuthorId);
        Assert.Equal("hello", pending.Body.Value);
    }

    [Fact]
    public async Task HandleAsync_WhenConversationDoesNotExist_ReturnsNotFound_WithoutEnqueueing()
    {
        var conversations = new FakeConversationRepository();
        var pipeline = new FakeMessagePipeline();
        var handler = new SendVisitorMessageHandler(conversations, new FakeRateLimiter(), new MessageSendRateLimitOptions(), pipeline);

        var result = await handler.HandleAsync(
            new SendVisitorMessage(new ConversationId(Guid.NewGuid()), VisitorId, "hello"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
        Assert.Empty(pipeline.Enqueued);
    }

    [Fact]
    public async Task HandleAsync_WhenThePerVisitorRateLimitIsExceeded_ReturnsRateLimited_BeforeTouchingTheConversationOrThePipeline()
    {
        var conversations = new FakeConversationRepository();
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversations.Seed(conversation);
        var limiter = new RateLimitedFakeRateLimiter(TimeSpan.FromSeconds(5));
        var pipeline = new FakeMessagePipeline();
        var handler = new SendVisitorMessageHandler(conversations, limiter, new MessageSendRateLimitOptions(), pipeline);

        var result = await handler.HandleAsync(
            new SendVisitorMessage(conversation.Id, VisitorId, "hello"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Message.RateLimited", result.Error!.Value.Code);
        Assert.Contains("5.0s", result.Error!.Value.Message);
        Assert.Empty(pipeline.Enqueued);
    }

    [Fact]
    public async Task HandleAsync_WhenTheBodyIsEmpty_ReturnsInvalidBody_WithoutEnqueueing()
    {
        var pipeline = new FakeMessagePipeline();
        var (handler, _, conversation, _) = CreateHandlerWithWaitingConversation(pipeline);

        var result = await handler.HandleAsync(
            new SendVisitorMessage(conversation.Id, VisitorId, "   "), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Message.InvalidBody", result.Error!.Value.Code);
        Assert.Empty(pipeline.Enqueued);
    }

    [Fact]
    public async Task HandleAsync_WhenThePipelineReportsAFailure_ForwardsItVerbatim()
    {
        var pipeline = new FakeMessagePipeline(Result<int>.Failure(
            ConversationErrors.Forbidden("This visitor is not a participant of this conversation.")));
        var (handler, _, conversation, _) = CreateHandlerWithWaitingConversation(pipeline);

        var result = await handler.HandleAsync(
            new SendVisitorMessage(conversation.Id, VisitorId, "hello"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }
}
