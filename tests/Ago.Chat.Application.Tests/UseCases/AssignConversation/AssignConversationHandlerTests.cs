using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.AssignConversation;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.AssignConversation;

public class AssignConversationHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static (AssignConversationHandler Handler, FakeConversationRepository Conversations, FakePermissionChecker Permissions, Conversation Conversation)
        CreateHandlerWithWaitingConversation(bool grantPermission = true)
    {
        var conversations = new FakeConversationRepository();
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversations.Seed(conversation);

        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ConversationAssign);
        }

        var handler = new AssignConversationHandler(conversations, permissions, new FakeClock(Now));
        return (handler, conversations, permissions, conversation);
    }

    [Fact]
    public async Task HandleAsync_WhenPermittedAndWaiting_Succeeds()
    {
        var (handler, _, _, conversation) = CreateHandlerWithWaitingConversation();

        var result = await handler.HandleAsync(
            new Application.UseCases.AssignConversation.AssignConversation(conversation.Id, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(ConversationState.Assigned, conversation.State);
        Assert.Equal(OperatorId, conversation.OperatorId);
    }

    [Fact]
    public async Task HandleAsync_WhenNotPermitted_ReturnsForbidden_BeforeTouchingTheConversation()
    {
        var (handler, _, _, conversation) = CreateHandlerWithWaitingConversation(grantPermission: false);

        var result = await handler.HandleAsync(
            new Application.UseCases.AssignConversation.AssignConversation(conversation.Id, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Equal(ConversationState.Waiting, conversation.State);
    }

    [Fact]
    public async Task HandleAsync_WhenConversationDoesNotExist_ReturnsNotFound()
    {
        var conversations = new FakeConversationRepository();
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationAssign);
        var handler = new AssignConversationHandler(conversations, permissions, new FakeClock(Now));

        var result = await handler.HandleAsync(
            new Application.UseCases.AssignConversation.AssignConversation(new ConversationId(Guid.NewGuid()), OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    /// <summary>
    /// `17-01`: the cross-tenant case, and the one this handler failed before that item. The operator
    /// genuinely holds `conversation:assign` - for <b>their own</b> site - and names a conversation
    /// belonging to a different one. The permission check passes (it is scoped to the caller's site,
    /// which is not the conversation's), so the belongs-to-site comparison is the only thing standing
    /// between this call and a cross-tenant assignment.
    ///
    /// <para>Why an assignment is the case that matters rather than one refusal among many: every
    /// other operator-facing conversation path gates on <c>conversation.OperatorId == RequestedBy</c>,
    /// so a successful cross-tenant assign converts all of them into "yes" for the caller - reading
    /// the thread, sending into it, closing it, downloading its attachments.</para>
    ///
    /// <para>NotFound rather than Forbidden, matching `DeleteAttachmentHandler`/
    /// `RevokeWebhookEndpointHandler`: another tenant's row must be indistinguishable from a
    /// nonexistent one.</para>
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenTheConversationBelongsToAnotherSite_ReturnsNotFound_AndLeavesItWaiting()
    {
        var conversations = new FakeConversationRepository();
        var victimSiteId = new SiteId(Guid.NewGuid());
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), victimSiteId, VisitorId, Now);
        conversations.Seed(conversation);

        // A real grant, on the caller's own site - the point is that a legitimately-permitted
        // operator is still refused, not that an unpermitted one is.
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationAssign);
        var handler = new AssignConversationHandler(conversations, permissions, new FakeClock(Now));

        var result = await handler.HandleAsync(
            new Application.UseCases.AssignConversation.AssignConversation(conversation.Id, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
        Assert.Equal(ConversationState.Waiting, conversation.State);
        Assert.Null(conversation.OperatorId);
    }

    [Fact]
    public async Task HandleAsync_WhenAlreadyAssigned_ReturnsInvalidState()
    {
        var (handler, conversations, permissions, conversation) = CreateHandlerWithWaitingConversation();
        conversation.AssignTo(new OperatorId(Guid.NewGuid()), Now);
        await conversations.SaveAsync(conversation, CancellationToken.None);

        var result = await handler.HandleAsync(
            new Application.UseCases.AssignConversation.AssignConversation(conversation.Id, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.InvalidState", result.Error!.Value.Code);
    }
}
