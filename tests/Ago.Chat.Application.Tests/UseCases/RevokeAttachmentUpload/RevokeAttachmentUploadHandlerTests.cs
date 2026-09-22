using System.Text.Json;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.RevokeAttachmentUpload;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.RevokeAttachmentUpload;

public class RevokeAttachmentUploadHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly ConversationId ConversationId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly OperatorId OtherOperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        RevokeAttachmentUploadHandler Handler, FakeConversationRepository Conversations,
        FakeConversationAttachmentUploadGrantRepository Grants, FakeOutboxWriter Outbox, FakeUnitOfWork UnitOfWork);

    private static Fixture CreateFixture(
        bool grantPermission = true, bool seedConversation = true, bool currentlyGranted = true,
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
            grants.SeedConversation(ConversationId, SiteId, currentlyGranted);
        }

        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ConversationAttachmentUploadGrant);
        }

        var outbox = new FakeOutboxWriter();
        var unitOfWork = new FakeUnitOfWork();
        var handler = new RevokeAttachmentUploadHandler(
            conversations, grants, permissions, unitOfWork, outbox, new FakeIdGenerator(), new FakeClock(Now));
        return new Fixture(handler, conversations, grants, outbox, unitOfWork);
    }

    [Fact]
    public async Task HandleAsync_WhenPermittedAndAssignedAndCurrentlyGranted_RevokesTheUpload_AndReturnsItsStatus()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RevokeAttachmentUpload.RevokeAttachmentUpload(ConversationId, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(ConversationId, result.Value.ConversationId);
        Assert.Equal(Now, result.Value.OccurredAt);
        Assert.Equal(OperatorId, result.Value.OperatorId);
        Assert.Equal(1, fixture.Grants.RevokeCalls);
    }

    // `25-110`: the item's own root cause, now fixed - a revoke that actually changes state must
    // enqueue AttachmentUploadGrantChanged(Granted: false) through the outbox, inside a committed
    // transaction, so a visitor whose connection is already open learns about it live rather than only
    // on a later reconnect.
    [Fact]
    public async Task HandleAsync_WhenCurrentlyGranted_EnqueuesAttachmentUploadGrantChanged_WithGrantedFalse_AndCommits()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RevokeAttachmentUpload.RevokeAttachmentUpload(ConversationId, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var envelope = Assert.Single(fixture.Outbox.Enqueued);
        Assert.Equal(nameof(AttachmentUploadGrantChanged), envelope.Type);
        var contract = JsonSerializer.Deserialize<AttachmentUploadGrantChanged>(envelope.Payload);
        Assert.Equal(ConversationId.Value, contract!.ConversationId);
        Assert.Equal(VisitorId.Value, contract.VisitorId);
        Assert.False(contract.Granted);
        Assert.Equal(Now, contract.OccurredAt);
        Assert.Equal(1, fixture.UnitOfWork.TransactionsBegun);
        Assert.Equal(1, fixture.UnitOfWork.TransactionsCommitted);
    }

    [Fact]
    public async Task HandleAsync_WhenTheOperatorLacksThePermission_ReturnsForbidden_AndRevokesNothing()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RevokeAttachmentUpload.RevokeAttachmentUpload(ConversationId, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Equal(0, fixture.Grants.RevokeCalls);
        Assert.Empty(fixture.Outbox.Enqueued);
        Assert.Equal(0, fixture.UnitOfWork.TransactionsBegun);
    }

    [Fact]
    public async Task HandleAsync_WhenTheOperatorHoldsThePermissionButIsNotAssigned_ReturnsForbidden_AndRevokesNothing()
    {
        var fixture = CreateFixture(assignedOperator: OtherOperatorId);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RevokeAttachmentUpload.RevokeAttachmentUpload(ConversationId, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Equal(0, fixture.Grants.RevokeCalls);
        Assert.Empty(fixture.Outbox.Enqueued);
    }

    [Fact]
    public async Task HandleAsync_WhenTheConversationDoesNotExist_ReturnsNotFound()
    {
        var fixture = CreateFixture(seedConversation: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RevokeAttachmentUpload.RevokeAttachmentUpload(ConversationId, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
        Assert.Empty(fixture.Outbox.Enqueued);
    }

    // `25-110`: "not on AlreadyInState - nothing changed, nothing to tell anyone" (the item's own
    // Scope). Already-not-granted is exactly that outcome for a revoke.
    [Fact]
    public async Task HandleAsync_WhenNotCurrentlyGranted_ReturnsConversationAttachmentUploadNotGranted_AndEnqueuesNothing()
    {
        var fixture = CreateFixture(currentlyGranted: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RevokeAttachmentUpload.RevokeAttachmentUpload(ConversationId, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.AttachmentUploadNotGranted", result.Error!.Value.Code);
        Assert.Empty(fixture.Outbox.Enqueued);
        // A transaction was opened (the conditional UPDATE still has to run to learn nothing changed)
        // but never committed - nothing for it to persist.
        Assert.Equal(1, fixture.UnitOfWork.TransactionsBegun);
        Assert.Equal(0, fixture.UnitOfWork.TransactionsCommitted);
    }

    // `23-78`: revoking a conversation whose grant came from the tenant-level default (no operator
    // behind it - `Conversation.Start`'s own remarks) is not a special case for this handler; the
    // repository's own `UPDATE ... WHERE attachment_upload_granted_at IS NOT NULL` does not care
    // whether `attachment_upload_granted_by` was ever populated.
    [Fact]
    public async Task HandleAsync_WhenTheGrantCameFromTheTenantDefault_StillRevokesIt()
    {
        var conversations = new FakeConversationRepository();
        var conversation = Conversation.Start(
            ConversationId, SiteId, VisitorId, Now, attachmentUploadGrantedByDefault: true);
        conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        conversation.AssignTo(OperatorId, Now);
        conversations.Seed(conversation);
        var grants = new FakeConversationAttachmentUploadGrantRepository();
        grants.SeedConversation(ConversationId, SiteId, granted: true);
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationAttachmentUploadGrant);
        var handler = new RevokeAttachmentUploadHandler(
            conversations, grants, permissions, new FakeUnitOfWork(), new FakeOutboxWriter(), new FakeIdGenerator(), new FakeClock(Now));

        var result = await handler.HandleAsync(
            new Application.UseCases.RevokeAttachmentUpload.RevokeAttachmentUpload(ConversationId, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, grants.RevokeCalls);
    }
}
