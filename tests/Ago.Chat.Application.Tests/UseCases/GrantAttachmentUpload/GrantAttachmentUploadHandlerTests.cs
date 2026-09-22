using System.Text.Json;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GrantAttachmentUpload;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GrantAttachmentUpload;

public class GrantAttachmentUploadHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly SiteId OtherSiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly ConversationId ConversationId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly OperatorId OtherOperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        GrantAttachmentUploadHandler Handler, FakeConversationRepository Conversations,
        FakeConversationAttachmentUploadGrantRepository Grants, FakeOutboxWriter Outbox, FakeUnitOfWork UnitOfWork);

    private static Fixture CreateFixture(
        bool grantPermission = true, bool seedConversation = true, bool alreadyGranted = false,
        OperatorId? assignedOperator = null)
    {
        var conversations = new FakeConversationRepository();
        var grants = new FakeConversationAttachmentUploadGrantRepository();
        if (seedConversation)
        {
            var conversation = Conversation.Start(ConversationId, SiteId, VisitorId, Now);
            // `25-221`: a brand-new conversation starts Pending, not Waiting - graduate it with the
            // visitor's own real first message before AssignTo, which still only accepts Waiting.
            conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
            conversation.AssignTo(assignedOperator ?? OperatorId, Now);
            conversations.Seed(conversation);
            grants.SeedConversation(ConversationId, SiteId, alreadyGranted);
        }

        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ConversationAttachmentUploadGrant);
        }

        var outbox = new FakeOutboxWriter();
        var unitOfWork = new FakeUnitOfWork();
        var handler = new GrantAttachmentUploadHandler(
            conversations, grants, permissions, unitOfWork, outbox, new FakeIdGenerator(), new FakeClock(Now));
        return new Fixture(handler, conversations, grants, outbox, unitOfWork);
    }

    [Fact]
    public async Task HandleAsync_WhenPermittedAndAssigned_GrantsTheUpload_AndReturnsItsStatus()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GrantAttachmentUpload.GrantAttachmentUpload(ConversationId, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(ConversationId, result.Value.ConversationId);
        Assert.Equal(Now, result.Value.OccurredAt);
        Assert.Equal(OperatorId, result.Value.OperatorId);
        Assert.Equal(1, fixture.Grants.GrantCalls);
    }

    // `25-110`: the item's own root cause, now fixed - a grant that actually changes state must
    // enqueue AttachmentUploadGrantChanged(Granted: true) through the outbox, inside a committed
    // transaction, so a visitor whose connection is already open learns about it live rather than only
    // on a later reconnect (`ago-widget`'s own `connection.ts` doc comment on
    // `onAttachmentUploadGrantChange` used to name this exact gap).
    [Fact]
    public async Task HandleAsync_WhenApplied_EnqueuesAttachmentUploadGrantChanged_WithGrantedTrue_AndCommits()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GrantAttachmentUpload.GrantAttachmentUpload(ConversationId, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var envelope = Assert.Single(fixture.Outbox.Enqueued);
        Assert.Equal(nameof(AttachmentUploadGrantChanged), envelope.Type);
        var contract = JsonSerializer.Deserialize<AttachmentUploadGrantChanged>(envelope.Payload);
        Assert.Equal(ConversationId.Value, contract!.ConversationId);
        Assert.Equal(VisitorId.Value, contract.VisitorId);
        Assert.True(contract.Granted);
        Assert.Equal(Now, contract.OccurredAt);
        Assert.Equal(1, fixture.UnitOfWork.TransactionsBegun);
        Assert.Equal(1, fixture.UnitOfWork.TransactionsCommitted);
    }

    [Fact]
    public async Task HandleAsync_WhenTheOperatorLacksThePermission_ReturnsForbidden_AndGrantsNothing()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GrantAttachmentUpload.GrantAttachmentUpload(ConversationId, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Equal(0, fixture.Grants.GrantCalls);
        Assert.Empty(fixture.Outbox.Enqueued);
        Assert.Equal(0, fixture.UnitOfWork.TransactionsBegun);
    }

    // `23-78`: the "RBAC answers may this operator act at all, a per-conversation comparison answers
    // on this one" split (GrantAttachmentUploadHandler's own remarks) - holding the permission is not
    // enough if this operator is not the one this conversation is assigned to.
    [Fact]
    public async Task HandleAsync_WhenTheOperatorHoldsThePermissionButIsNotAssigned_ReturnsForbidden_AndGrantsNothing()
    {
        var fixture = CreateFixture(assignedOperator: OtherOperatorId);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GrantAttachmentUpload.GrantAttachmentUpload(ConversationId, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Equal(0, fixture.Grants.GrantCalls);
        Assert.Empty(fixture.Outbox.Enqueued);
    }

    [Fact]
    public async Task HandleAsync_WhenTheConversationDoesNotExist_ReturnsNotFound()
    {
        var fixture = CreateFixture(seedConversation: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GrantAttachmentUpload.GrantAttachmentUpload(ConversationId, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
        Assert.Empty(fixture.Outbox.Enqueued);
    }

    // `25-110`: "not on AlreadyInState - nothing changed, nothing to tell anyone" (the item's own
    // Scope).
    [Fact]
    public async Task HandleAsync_WhenAlreadyGranted_ReturnsConversationAttachmentUploadAlreadyGranted_AndEnqueuesNothing()
    {
        var fixture = CreateFixture(alreadyGranted: true);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GrantAttachmentUpload.GrantAttachmentUpload(ConversationId, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.AttachmentUploadAlreadyGranted", result.Error!.Value.Code);
        Assert.Empty(fixture.Outbox.Enqueued);
        // A transaction was opened (the conditional UPDATE still has to run to learn nothing changed)
        // but never committed - nothing for it to persist.
        Assert.Equal(1, fixture.UnitOfWork.TransactionsBegun);
        Assert.Equal(0, fixture.UnitOfWork.TransactionsCommitted);
    }

    // `23-78`: unlike `IConversationRepository.GetByIdAsync` (no site filter at all - the same shape
    // `CloseConversationHandler`'s own load has, where the `OperatorId` comparison is what actually
    // enforces tenant scoping in production, since an operator belongs to exactly one site),
    // `IConversationAttachmentUploadGrantRepository.GrantAsync` scopes its own write by
    // `(conversationId, siteId)` - the real repository's `WHERE ... and site_id = @siteId`. This test
    // seeds no grant-repository row for `(ConversationId, SiteId)` at all (only, implicitly, nothing
    // for `OtherSiteId` either), so the not-found answer below comes from that repository-level scope,
    // not from the aggregate lookup - the same defense-in-depth `IConversationBlockRepository`'s own
    // scoped write already provides for its sibling action.
    [Fact]
    public async Task HandleAsync_WhenTheConversationBelongsToADifferentSite_ReturnsNotFound_NotForbidden()
    {
        var conversations = new FakeConversationRepository();
        var conversation = Conversation.Start(ConversationId, OtherSiteId, VisitorId, Now);
        conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        conversation.AssignTo(OperatorId, Now);
        conversations.Seed(conversation);
        var grants = new FakeConversationAttachmentUploadGrantRepository();
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationAttachmentUploadGrant);
        var handler = new GrantAttachmentUploadHandler(
            conversations, grants, permissions, new FakeUnitOfWork(), new FakeOutboxWriter(), new FakeIdGenerator(), new FakeClock(Now));

        var result = await handler.HandleAsync(
            new Application.UseCases.GrantAttachmentUpload.GrantAttachmentUpload(ConversationId, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }
}
