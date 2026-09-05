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

    private static (AssignConversationHandler Handler, FakeConversationRepository Conversations, FakePermissionChecker Permissions, FakeConversationAssignmentLog AssignmentLog, FakeOperatorCapacity Capacity, FakeUnitOfWork UnitOfWork, Conversation Conversation)
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

        var assignmentLog = new FakeConversationAssignmentLog();
        var capacity = new FakeOperatorCapacity();
        var unitOfWork = new FakeUnitOfWork();
        var handler = new AssignConversationHandler(
            conversations, assignmentLog, permissions, capacity, unitOfWork, new FakeIdGenerator(), new FakeClock(Now));
        return (handler, conversations, permissions, assignmentLog, capacity, unitOfWork, conversation);
    }

    [Fact]
    public async Task HandleAsync_WhenPermittedAndWaiting_Succeeds()
    {
        var (handler, _, _, _, _, _, conversation) = CreateHandlerWithWaitingConversation();

        var result = await handler.HandleAsync(
            new Application.UseCases.AssignConversation.AssignConversation(conversation.Id, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(ConversationState.Assigned, conversation.State);
        Assert.Equal(OperatorId, conversation.OperatorId);
    }

    /// <summary>
    /// `23-04`'s own Done-when, at the level a handler unit test can prove: a real take opens exactly
    /// one interval, naming this operator and conversation, with source <c>Taken</c> - not
    /// <c>Assigned</c>, which this same handler wrote before this item gave the path its own reachable
    /// UI and its own value (<see cref="ConversationAssignmentSource.Taken"/>'s own remarks). The take
    /// also holds a real capacity claim and commits inside a real transaction.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenPermittedAndWaiting_OpensATakenInterval_AndClaimsCapacityUnconditionally()
    {
        var (handler, _, _, assignmentLog, capacity, unitOfWork, conversation) = CreateHandlerWithWaitingConversation();

        var result = await handler.HandleAsync(
            new Application.UseCases.AssignConversation.AssignConversation(conversation.Id, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var interval = Assert.Single(assignmentLog.Opened);
        Assert.Equal(conversation.Id, interval.ConversationId);
        Assert.Equal(OperatorId, interval.OperatorId);
        Assert.Equal(SiteId, interval.SiteId);
        Assert.Equal(ConversationAssignmentSource.Taken, interval.Source);
        Assert.Equal(Now, interval.StartedAt);
        Assert.Null(interval.EndedAt);

        // The compare-free write, not TryClaimAsync - decisions.md §2's "a manual claim increments
        // active_chats and does not check it".
        Assert.Equal([OperatorId], capacity.UnconditionalClaims);
        Assert.Empty(capacity.Claims);
        Assert.True(conversation.HoldsCapacityClaim);

        Assert.Equal(1, unitOfWork.TransactionsBegun);
        Assert.Equal(1, unitOfWork.TransactionsCommitted);
    }

    /// <summary>
    /// `23-03`'s own Done-when, still true under this item's rewrite: "A hub reconnect by the same
    /// operator adds no row." `Conversation.AssignTo`'s own same-operator no-op returns before raising
    /// <see cref="ConversationAssigned"/>, and this handler's interval write and capacity claim both sit
    /// behind that event - proven here by calling assign twice for the identical operator, the same
    /// shape `OperatorHub.JoinConversationAsync` produces on every reconnect. `23-04`'s own Scope: "a
    /// reconnect must not increment a second time" - <see cref="FakeOperatorCapacity.UnconditionalClaims"/>
    /// staying at one element across both calls is exactly that assertion.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenTheSameOperatorReconnects_OpensNoSecondInterval_AndClaimsNoSecondSlot()
    {
        var (handler, _, _, assignmentLog, capacity, unitOfWork, conversation) = CreateHandlerWithWaitingConversation();
        var command = new Application.UseCases.AssignConversation.AssignConversation(conversation.Id, OperatorId, SiteId);

        var first = await handler.HandleAsync(command, CancellationToken.None);
        Assert.True(first.IsSuccess);
        Assert.Single(assignmentLog.Opened);
        Assert.Single(capacity.UnconditionalClaims);
        Assert.Equal(1, unitOfWork.TransactionsBegun);

        var second = await handler.HandleAsync(command, CancellationToken.None);

        Assert.True(second.IsSuccess);
        Assert.Single(assignmentLog.Opened);
        // No second claim, and no second transaction at all - the no-op path returns before
        // IUnitOfWork.BeginTransactionAsync is ever called (AssignAndSaveAsync's own remarks).
        Assert.Single(capacity.UnconditionalClaims);
        Assert.Equal(1, unitOfWork.TransactionsBegun);
    }

    [Fact]
    public async Task HandleAsync_WhenNotPermitted_ReturnsForbidden_BeforeTouchingTheConversation()
    {
        var (handler, _, _, assignmentLog, capacity, _, conversation) = CreateHandlerWithWaitingConversation(grantPermission: false);

        var result = await handler.HandleAsync(
            new Application.UseCases.AssignConversation.AssignConversation(conversation.Id, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Equal(ConversationState.Waiting, conversation.State);
        Assert.Empty(assignmentLog.Opened);
        Assert.Empty(capacity.UnconditionalClaims);
    }

    [Fact]
    public async Task HandleAsync_WhenConversationDoesNotExist_ReturnsNotFound()
    {
        var conversations = new FakeConversationRepository();
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationAssign);
        var handler = new AssignConversationHandler(
            conversations, new FakeConversationAssignmentLog(), permissions, new FakeOperatorCapacity(),
            new FakeUnitOfWork(), new FakeIdGenerator(), new FakeClock(Now));

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
        var assignmentLog = new FakeConversationAssignmentLog();
        var handler = new AssignConversationHandler(
            conversations, assignmentLog, permissions, new FakeOperatorCapacity(), new FakeUnitOfWork(),
            new FakeIdGenerator(), new FakeClock(Now));

        var result = await handler.HandleAsync(
            new Application.UseCases.AssignConversation.AssignConversation(conversation.Id, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
        Assert.Equal(ConversationState.Waiting, conversation.State);
        Assert.Null(conversation.OperatorId);
        Assert.Empty(assignmentLog.Opened);
    }

    [Fact]
    public async Task HandleAsync_WhenAlreadyAssigned_ReturnsInvalidState()
    {
        var (handler, conversations, _, assignmentLog, capacity, _, conversation) = CreateHandlerWithWaitingConversation();
        conversation.AssignTo(new OperatorId(Guid.NewGuid()), Now);
        await conversations.SaveAsync(conversation, CancellationToken.None);
        assignmentLog.Opened.Clear();

        var result = await handler.HandleAsync(
            new Application.UseCases.AssignConversation.AssignConversation(conversation.Id, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.InvalidState", result.Error!.Value.Code);
        Assert.Empty(assignmentLog.Opened);
        // The loser of a race must never be charged for a slot it never actually got - checked here at
        // the same "already assigned to somebody else" refusal, before any transaction is opened.
        Assert.Empty(capacity.UnconditionalClaims);
    }

    /// <summary>
    /// `23-04`'s own Scope: a claim's transaction losing every retry attempt to write contention on the
    /// `operators` row returns a clean, named `Result` failure - never an unhandled exception - the
    /// same "an operator never sees `40P01`" guarantee `TransferConversationHandler` already gives for
    /// its own transaction.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenCapacityClaimAlwaysLosesToContention_ReturnsClaimContended()
    {
        var (handler, _, _, _, capacity, _, conversation) = CreateHandlerWithWaitingConversation();
        capacity.UnconditionalClaimAlwaysLosesToContention = true;

        var result = await handler.HandleAsync(
            new Application.UseCases.AssignConversation.AssignConversation(conversation.Id, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.ClaimContended", result.Error!.Value.Code);
    }
}
