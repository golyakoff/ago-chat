using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.CloseConversation;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.CloseConversation;

public class CloseConversationHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        CloseConversationHandler Handler,
        FakeConversationRepository Conversations,
        FakePermissionChecker Permissions,
        FakeOutboxWriter Outbox,
        Conversation Conversation);

    private static Fixture CreateHandlerWithAssignedConversation(bool grantPermission = true)
    {
        var conversations = new FakeConversationRepository();
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversation.AssignTo(OperatorId, Now);
        // Simulates a fresh load from Postgres (EF's materialization ctor never raises domain events) -
        // without this, the leftover ConversationAssigned from AssignTo above would still be sitting in
        // the aggregate's in-memory list, which a real GetByIdAsync would never hand back.
        conversation.ClearDomainEvents();
        conversations.Seed(conversation);

        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ConversationClose);
        }

        var outbox = new FakeOutboxWriter();
        var handler = new CloseConversationHandler(
            conversations, permissions, outbox, new FakeIdGenerator(), new FakeClock(Now));
        return new Fixture(handler, conversations, permissions, outbox, conversation);
    }

    [Fact]
    public async Task HandleAsync_WhenPermittedAndAssignedToThisOperator_ClosesAndStagesTheOutboxRow()
    {
        var fixture = CreateHandlerWithAssignedConversation();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.CloseConversation.CloseConversation(fixture.Conversation.Id, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(ConversationState.Closed, fixture.Conversation.State);

        // `6-02`: the mapper's own output, proven the same way ConfirmAttachmentHandlerTests already
        // proves AttachmentConfirmedMapper's - the real transaction guarantee is Ago.Chat.Integration.
        // Tests' job, this only proves the handler enqueues the right envelope at all.
        var envelope = Assert.Single(fixture.Outbox.Enqueued);
        Assert.Equal(nameof(ConversationEnded), envelope.Type);
        Assert.Equal(fixture.Conversation.Id.Value, envelope.MessageId);
        Assert.Equal(fixture.Conversation.Id.Value.ToString(), envelope.PartitionKey);
    }

    [Fact]
    public async Task HandleAsync_WhenNotPermitted_ReturnsForbidden_BeforeTouchingTheConversation()
    {
        var fixture = CreateHandlerWithAssignedConversation(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.CloseConversation.CloseConversation(fixture.Conversation.Id, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Equal(ConversationState.Assigned, fixture.Conversation.State);
        Assert.Empty(fixture.Outbox.Enqueued);
    }

    [Fact]
    public async Task HandleAsync_WhenTheOperatorIsNotAssignedToThisConversation_ReturnsForbidden()
    {
        var fixture = CreateHandlerWithAssignedConversation();
        var someoneElse = new OperatorId(Guid.NewGuid());
        fixture.Permissions.Grant(someoneElse, SiteId, Permission.ConversationClose);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.CloseConversation.CloseConversation(fixture.Conversation.Id, someoneElse, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Equal(ConversationState.Assigned, fixture.Conversation.State);
        Assert.Empty(fixture.Outbox.Enqueued);
    }

    [Fact]
    public async Task HandleAsync_WhenAlreadyClosed_ReturnsInvalidState()
    {
        // `6-02`'s own Done-when bar: Conversation.Close()'s existing domain invariant (no re-closing),
        // now actually reachable through a real call path for the first time - the same operator who
        // legitimately closed it once tries again.
        var fixture = CreateHandlerWithAssignedConversation();
        var command = new Application.UseCases.CloseConversation.CloseConversation(fixture.Conversation.Id, OperatorId, SiteId);
        var first = await fixture.Handler.HandleAsync(command, CancellationToken.None);
        Assert.True(first.IsSuccess);

        var result = await fixture.Handler.HandleAsync(command, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.InvalidState", result.Error!.Value.Code);
        // Only the first close's envelope - the rejected retry stages nothing new.
        Assert.Single(fixture.Outbox.Enqueued);
    }

    [Fact]
    public async Task HandleAsync_WhenConversationDoesNotExist_ReturnsNotFound()
    {
        var conversations = new FakeConversationRepository();
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationClose);
        var handler = new CloseConversationHandler(
            conversations, permissions, new FakeOutboxWriter(), new FakeIdGenerator(), new FakeClock(Now));

        var result = await handler.HandleAsync(
            new Application.UseCases.CloseConversation.CloseConversation(new ConversationId(Guid.NewGuid()), OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }
}
