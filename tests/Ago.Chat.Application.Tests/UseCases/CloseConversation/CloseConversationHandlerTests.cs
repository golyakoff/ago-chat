using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.CloseConversation;
using Ago.Chat.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
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
        FakeOperatorCapacity Capacity,
        Conversation Conversation);

    private static Fixture CreateHandlerWithAssignedConversation(
        bool grantPermission = true, bool holdsCapacityClaim = true)
    {
        var conversations = new FakeConversationRepository();
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        // `6-09`: defaults to the engine-assigned case, which is the one that has a claim to release.
        // The hand-picked case (holdsCapacityClaim: false) has its own test below.
        conversation.AssignTo(OperatorId, Now, holdsCapacityClaim);
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
        var capacity = new FakeOperatorCapacity();
        var handler = new CloseConversationHandler(
            conversations, permissions, capacity, outbox, new FakeIdGenerator(), new FakeClock(Now),
            NullLogger<CloseConversationHandler>.Instance);
        return new Fixture(handler, conversations, permissions, outbox, capacity, conversation);
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

    /// <summary>
    /// `6-10`: the close has already committed by the time the release is attempted, so a release that
    /// loses to `operators`-row contention must not turn a successful close into a failed request. The
    /// operator is told the conversation closed - because it did - and the cost is one leaked capacity
    /// slot, the same residual `6-09` already accepts for a process death in this window, recovered by
    /// `4-04`'s disconnect sweep. Failing instead would be worse in both directions: it would report a
    /// state change that actually happened as not having happened, and the retry it invites would be
    /// rejected as already-closed without recovering the slot either.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenTheCapacityReleaseLosesToContention_StillReportsTheCloseAsSuccessful()
    {
        var fixture = CreateHandlerWithAssignedConversation();
        fixture.Capacity.ReleaseAlwaysLosesToContention = true;

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.CloseConversation.CloseConversation(fixture.Conversation.Id, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(ConversationState.Closed, fixture.Conversation.State);
        // The release was genuinely attempted, once - not skipped, and not retried here (the bounded
        // retry belongs to the adapter, which owns the transaction the retry re-issues into).
        Assert.Equal([OperatorId], fixture.Capacity.Releases);
        // The conversation's receipt is spent either way: it was consumed inside the committed save,
        // so nothing here may hand it out a second time.
        Assert.False(fixture.Conversation.HoldsCapacityClaim);
        Assert.Single(fixture.Outbox.Enqueued);
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
    public async Task HandleAsync_WhenTheConversationHoldsACapacityClaim_ReleasesItForTheAssignedOperator()
    {
        // `6-09`: the whole point of the item, at the level that shows the *decision* - the engine
        // took a slot for this conversation, so closing it gives that slot back. Before this item the
        // handler had no IOperatorCapacity at all and active_chats only ever came down when the
        // operator's last connection dropped.
        var fixture = CreateHandlerWithAssignedConversation();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.CloseConversation.CloseConversation(fixture.Conversation.Id, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(OperatorId, Assert.Single(fixture.Capacity.Releases));
        // The receipt is spent, which is what makes a second release structurally impossible.
        Assert.False(fixture.Conversation.HoldsCapacityClaim);
    }

    [Fact]
    public async Task HandleAsync_WhenTheConversationWasHandPicked_ReleasesNothing()
    {
        // The asymmetry this item had to resolve rather than assume away: AssignConversationHandler
        // (behind OperatorHub.JoinConversationAsync) never calls TryClaimAsync, so there is no slot
        // behind a hand-picked conversation. Releasing one anyway would decrement a slot some *other*
        // conversation is holding, letting the engine over-subscribe this operator - a worse bug than
        // the leak, and one the floor-at-zero in OperatorCapacityStore.ReleaseAsync would not catch.
        var fixture = CreateHandlerWithAssignedConversation(holdsCapacityClaim: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.CloseConversation.CloseConversation(fixture.Conversation.Id, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(ConversationState.Closed, fixture.Conversation.State);
        Assert.Empty(fixture.Capacity.Releases);
    }

    [Fact]
    public async Task HandleAsync_WhenAlreadyClosed_ReleasesNothingASecondTime()
    {
        var fixture = CreateHandlerWithAssignedConversation();
        var command = new Application.UseCases.CloseConversation.CloseConversation(fixture.Conversation.Id, OperatorId, SiteId);
        Assert.True((await fixture.Handler.HandleAsync(command, CancellationToken.None)).IsSuccess);

        var result = await fixture.Handler.HandleAsync(command, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Single(fixture.Capacity.Releases);
    }

    [Fact]
    public async Task HandleAsync_WhenNotPermitted_ReleasesNothing()
    {
        var fixture = CreateHandlerWithAssignedConversation(grantPermission: false);

        await fixture.Handler.HandleAsync(
            new Application.UseCases.CloseConversation.CloseConversation(fixture.Conversation.Id, OperatorId, SiteId),
            CancellationToken.None);

        Assert.Empty(fixture.Capacity.Releases);
        Assert.True(fixture.Conversation.HoldsCapacityClaim);
    }

    [Fact]
    public async Task HandleAsync_WhenConversationDoesNotExist_ReturnsNotFound()
    {
        var conversations = new FakeConversationRepository();
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationClose);
        var handler = new CloseConversationHandler(
            conversations, permissions, new FakeOperatorCapacity(), new FakeOutboxWriter(),
            new FakeIdGenerator(), new FakeClock(Now), NullLogger<CloseConversationHandler>.Instance);

        var result = await handler.HandleAsync(
            new Application.UseCases.CloseConversation.CloseConversation(new ConversationId(Guid.NewGuid()), OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }
}
