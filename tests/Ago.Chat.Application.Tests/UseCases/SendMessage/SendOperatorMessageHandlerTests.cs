using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases;
using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Tests.UseCases.SendMessage;

/// <summary>
/// `4-05`: same shrink as `SendVisitorMessageHandlerTests` - RBAC and body-shape checks stay here
/// (no conversation load needed for either, see the handler's own remarks), the participant/state
/// checks `AddOperatorMessage` enforces and the actual write move to
/// `Ago.Chat.Integration.Tests.MessageBatchWriterTests`.
/// </summary>
public class SendOperatorMessageHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly ConversationId ConversationId = new(Guid.NewGuid());

    private static (SendOperatorMessageHandler Handler, FakePermissionChecker Permissions, FakeMessagePipeline Pipeline)
        CreateHandler(bool grantPermission = true, FakeMessagePipeline? pipeline = null)
    {
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ConversationSend);
        }

        pipeline ??= new FakeMessagePipeline();
        var handler = new SendOperatorMessageHandler(permissions, pipeline);
        return (handler, permissions, pipeline);
    }

    [Fact]
    public async Task HandleAsync_WhenPermitted_EnqueuesTheMessageAndReturnsThePipelinesResult()
    {
        var pipeline = new FakeMessagePipeline(7);
        var (handler, _, _) = CreateHandler(pipeline: pipeline);

        var result = await handler.HandleAsync(
            new SendOperatorMessage(ConversationId, OperatorId, SiteId, "how can I help?"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(7, result.Value);
        var pending = Assert.Single(pipeline.Enqueued);
        Assert.Equal(ConversationId, pending.ConversationId);
        Assert.Equal(MessageAuthorKind.Operator, pending.AuthorKind);
        Assert.Equal(OperatorId.Value, pending.AuthorId);
        Assert.Equal("how can I help?", pending.Body.Value);
    }

    [Fact]
    public async Task HandleAsync_WhenTheOperatorLacksThePermission_ReturnsForbidden_WithoutEnqueueing()
    {
        var (handler, _, pipeline) = CreateHandler(grantPermission: false);

        var result = await handler.HandleAsync(
            new SendOperatorMessage(ConversationId, OperatorId, SiteId, "hi"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Empty(pipeline.Enqueued);
    }

    [Fact]
    public async Task HandleAsync_WhenTheBodyIsEmpty_ReturnsInvalidBody_WithoutEnqueueing()
    {
        var (handler, _, pipeline) = CreateHandler();

        var result = await handler.HandleAsync(
            new SendOperatorMessage(ConversationId, OperatorId, SiteId, "   "), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Message.InvalidBody", result.Error!.Value.Code);
        Assert.Empty(pipeline.Enqueued);
    }

    [Fact]
    public async Task HandleAsync_WhenThePipelineReportsAFailure_ForwardsItVerbatim()
    {
        var pipeline = new FakeMessagePipeline(Result<int>.Failure(
            ConversationErrors.Forbidden("This operator is not assigned to this conversation.")));
        var (handler, _, _) = CreateHandler(pipeline: pipeline);

        var result = await handler.HandleAsync(
            new SendOperatorMessage(ConversationId, OperatorId, SiteId, "hi"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }
}
