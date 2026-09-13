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

    private static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    private static (SendOperatorMessageHandler Handler, FakePermissionChecker Permissions, FakeMessagePipeline Pipeline, FakeSiteSuspensionReadStore Suspensions)
        CreateHandler(bool grantPermission = true, FakeMessagePipeline? pipeline = null, FakeSiteSuspensionReadStore? suspensions = null)
    {
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ConversationSend);
        }

        pipeline ??= new FakeMessagePipeline();
        suspensions ??= new FakeSiteSuspensionReadStore();
        var handler = new SendOperatorMessageHandler(permissions, suspensions, pipeline, new FakeClock(Now));
        return (handler, permissions, pipeline, suspensions);
    }

    [Fact]
    public async Task HandleAsync_WhenPermitted_EnqueuesTheMessageAndReturnsThePipelinesResult()
    {
        var pipeline = new FakeMessagePipeline(7);
        var (handler, _, _, _) = CreateHandler(pipeline: pipeline);

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
        var (handler, _, pipeline, _) = CreateHandler(grantPermission: false);

        var result = await handler.HandleAsync(
            new SendOperatorMessage(ConversationId, OperatorId, SiteId, "hi"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Empty(pipeline.Enqueued);
    }

    [Fact]
    public async Task HandleAsync_WhenTheBodyIsEmpty_ReturnsInvalidBody_WithoutEnqueueing()
    {
        var (handler, _, pipeline, _) = CreateHandler();

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
        var (handler, _, _, _) = CreateHandler(pipeline: pipeline);

        var result = await handler.HandleAsync(
            new SendOperatorMessage(ConversationId, OperatorId, SiteId, "hi"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    /// <summary>`22-08`: the operator-send suspension gate - a permitted operator on a currently
    /// suspended site is refused before the message is ever bound or enqueued.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheSiteIsSuspended_ReturnsCannotSend_WithoutEnqueueing()
    {
        var suspensions = new FakeSiteSuspensionReadStore();
        suspensions.Suspend(SiteId, Now.AddMinutes(30));
        var (handler, _, pipeline, _) = CreateHandler(suspensions: suspensions);

        var result = await handler.HandleAsync(
            new SendOperatorMessage(ConversationId, OperatorId, SiteId, "hi"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("TenantSuspension.CannotSend", result.Error!.Value.Code);
        Assert.Empty(pipeline.Enqueued);
    }

    /// <summary>The mirror of the test above - a suspension that has already passed must not gate
    /// anything, the identical "expiry is checked live" property `ISiteSuspensionReadStore`'s own
    /// remarks state.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheSitesSuspensionHasAlreadyPassed_SendsNormally()
    {
        var suspensions = new FakeSiteSuspensionReadStore();
        suspensions.Suspend(SiteId, Now.AddMinutes(-1));
        var pipeline = new FakeMessagePipeline(3);
        var (handler, _, _, _) = CreateHandler(pipeline: pipeline, suspensions: suspensions);

        var result = await handler.HandleAsync(
            new SendOperatorMessage(ConversationId, OperatorId, SiteId, "hi"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(pipeline.Enqueued);
    }
}
