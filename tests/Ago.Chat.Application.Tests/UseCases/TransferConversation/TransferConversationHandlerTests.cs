using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.TransferConversation;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.TransferConversation;

public class TransferConversationHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId FromOperatorId = new(Guid.NewGuid());
    private static readonly OperatorId ToOperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        Application.UseCases.TransferConversation.TransferConversationHandler Handler,
        FakeConversationRepository Conversations,
        FakeOperatorRepository Operators,
        FakePermissionChecker Permissions,
        FakeOutboxWriter Outbox,
        FakeOperatorCapacity Capacity,
        FakeUnitOfWork UnitOfWork,
        Conversation Conversation);

    private static Fixture CreateHandlerWithAssignedConversation(
        bool grantPermission = true,
        bool holdsCapacityClaim = true,
        bool targetHoldsSeat = true,
        DateTimeOffset? targetRemovedAt = null,
        bool seedTarget = true)
    {
        var conversations = new FakeConversationRepository();
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversation.AssignTo(FromOperatorId, Now, holdsCapacityClaim);
        // Simulates a fresh load from Postgres, exactly like CloseConversationHandlerTests' own
        // fixture - EF's materialization ctor never raises domain events, so a real GetByIdAsync
        // would never hand back the leftover ConversationAssigned from AssignTo above.
        conversation.ClearDomainEvents();
        conversations.Seed(conversation);

        var operators = new FakeOperatorRepository();
        if (seedTarget)
        {
            operators.Seed(new Operator(
                ToOperatorId, SiteId, OperatorStatus.Online, capacity: 5, holdsSeat: targetHoldsSeat, removedAt: targetRemovedAt));
        }

        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(FromOperatorId, SiteId, Permission.ConversationAssign);
        }

        var outbox = new FakeOutboxWriter();
        var capacity = new FakeOperatorCapacity();
        var unitOfWork = new FakeUnitOfWork();
        var handler = new Application.UseCases.TransferConversation.TransferConversationHandler(
            conversations, operators, permissions, capacity, unitOfWork, outbox, new FakeIdGenerator(), new FakeClock(Now));
        return new Fixture(handler, conversations, operators, permissions, outbox, capacity, unitOfWork, conversation);
    }

    private static Application.UseCases.TransferConversation.TransferConversation Command(
        Fixture fixture, OperatorId? to = null) =>
        new(fixture.Conversation.Id, FromOperatorId, to ?? ToOperatorId, SiteId);

    [Fact]
    public async Task HandleAsync_WhenFromEqualsTo_ReturnsTransferTargetIsCurrentOperator_BeforeTouchingAnything()
    {
        var fixture = CreateHandlerWithAssignedConversation(grantPermission: false, seedTarget: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.TransferConversation.TransferConversation(
                fixture.Conversation.Id, FromOperatorId, FromOperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.TransferTargetIsCurrentOperator", result.Error!.Value.Code);
        // Cheapest possible rejection - not even the permission check ran.
        Assert.Empty(fixture.Outbox.Enqueued);
        Assert.Equal(0, fixture.UnitOfWork.TransactionsBegun);
    }

    [Fact]
    public async Task HandleAsync_WhenNotPermitted_ReturnsForbidden()
    {
        var fixture = CreateHandlerWithAssignedConversation(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(Command(fixture), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Equal(ConversationState.Assigned, fixture.Conversation.State);
        Assert.Equal(FromOperatorId, fixture.Conversation.OperatorId);
        Assert.Empty(fixture.Outbox.Enqueued);
    }

    [Fact]
    public async Task HandleAsync_WhenTargetOperatorDoesNotExist_ReturnsTransferTargetNotEligible()
    {
        var fixture = CreateHandlerWithAssignedConversation(seedTarget: false);

        var result = await fixture.Handler.HandleAsync(Command(fixture), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.TransferTargetNotEligible", result.Error!.Value.Code);
        Assert.Equal(FromOperatorId, fixture.Conversation.OperatorId);
        Assert.Empty(fixture.Outbox.Enqueued);
    }

    /// <summary>
    /// `18-02`'s own HoldsSeat/RemovedAt decision, proven rather than argued: a seat-less operator
    /// cannot sign in (`13-03`'s own mechanism), so a transfer that let one become the new assignee
    /// would hand the conversation to nobody who could ever answer it - refused here, visibly, rather
    /// than left for capacity or sign-in to make moot after the fact.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenTargetHasNoSeat_ReturnsTransferTargetNotEligible()
    {
        var fixture = CreateHandlerWithAssignedConversation(targetHoldsSeat: false);

        var result = await fixture.Handler.HandleAsync(Command(fixture), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.TransferTargetNotEligible", result.Error!.Value.Code);
        Assert.Equal(FromOperatorId, fixture.Conversation.OperatorId);
        Assert.Empty(fixture.Capacity.Claims);
    }

    /// <summary>The other half of the same decision - a removed operator, distinct state from
    /// seat-less but resolving to the identical refusal.</summary>
    [Fact]
    public async Task HandleAsync_WhenTargetIsRemoved_ReturnsTransferTargetNotEligible()
    {
        var fixture = CreateHandlerWithAssignedConversation(targetRemovedAt: Now.AddDays(-1));

        var result = await fixture.Handler.HandleAsync(Command(fixture), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.TransferTargetNotEligible", result.Error!.Value.Code);
        Assert.Empty(fixture.Capacity.Claims);
    }

    [Fact]
    public async Task HandleAsync_WhenConversationDoesNotExist_ReturnsNotFound()
    {
        var fixture = CreateHandlerWithAssignedConversation();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.TransferConversation.TransferConversation(
                new ConversationId(Guid.NewGuid()), FromOperatorId, ToOperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenTheCallerIsNotAssignedToThisConversation_ReturnsForbidden()
    {
        var fixture = CreateHandlerWithAssignedConversation();
        var someoneElse = new OperatorId(Guid.NewGuid());
        fixture.Permissions.Grant(someoneElse, SiteId, Permission.ConversationAssign);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.TransferConversation.TransferConversation(
                fixture.Conversation.Id, someoneElse, ToOperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Equal(FromOperatorId, fixture.Conversation.OperatorId);
    }

    [Fact]
    public async Task HandleAsync_WhenAssignedAndPermitted_MovesTheConversation_ClaimsTarget_ReleasesSource_AndCommits()
    {
        var fixture = CreateHandlerWithAssignedConversation();

        var result = await fixture.Handler.HandleAsync(Command(fixture), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Message : string.Empty);
        Assert.Equal(ConversationState.Assigned, fixture.Conversation.State);
        Assert.Equal(ToOperatorId, fixture.Conversation.OperatorId);
        Assert.Equal(ToOperatorId, Assert.Single(fixture.Capacity.Claims));
        Assert.Equal(FromOperatorId, Assert.Single(fixture.Capacity.Releases));
        Assert.Equal(1, fixture.UnitOfWork.TransactionsBegun);
        Assert.Equal(1, fixture.UnitOfWork.TransactionsCommitted);

        // `18-02`: reuses the existing ConversationAssignedToOperator wire contract - see
        // ConversationTransferredMapper's own remarks for why. Proven at this level the same way
        // CloseConversationHandlerTests proves ConversationClosedMapper's output: the real transaction
        // guarantee is Ago.Chat.Integration.Tests' job, this only proves the handler enqueues the
        // right envelope at all.
        var envelope = Assert.Single(fixture.Outbox.Enqueued);
        Assert.Equal(nameof(ConversationAssignedToOperator), envelope.Type);
        Assert.Equal(fixture.Conversation.Id.Value.ToString(), envelope.PartitionKey);
    }

    [Fact]
    public async Task HandleAsync_WhenTheConversationHoldsNoCapacityClaim_TransfersWithoutTouchingCapacity()
    {
        var fixture = CreateHandlerWithAssignedConversation(holdsCapacityClaim: false);

        var result = await fixture.Handler.HandleAsync(Command(fixture), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(ToOperatorId, fixture.Conversation.OperatorId);
        Assert.False(fixture.Conversation.HoldsCapacityClaim);
        Assert.Empty(fixture.Capacity.Claims);
        Assert.Empty(fixture.Capacity.Releases);
        Assert.Equal(1, fixture.UnitOfWork.TransactionsCommitted);
    }

    /// <summary>The backlog item's own Scope: "Refuse rather than queue when the target is at
    /// capacity, and say so in the interface." Nothing commits, and the conversation stays exactly
    /// where it started.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheTargetIsAtCapacity_RefusesVisibly_AndLeavesEverythingUnchanged()
    {
        var fixture = CreateHandlerWithAssignedConversation();
        fixture.Capacity.ClaimFailsFor.Add(ToOperatorId);

        var result = await fixture.Handler.HandleAsync(Command(fixture), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.TransferTargetAtCapacity", result.Error!.Value.Code);
        Assert.Equal(ConversationState.Assigned, fixture.Conversation.State);
        Assert.Equal(FromOperatorId, fixture.Conversation.OperatorId);
        Assert.True(fixture.Conversation.HoldsCapacityClaim);
        Assert.Empty(fixture.Outbox.Enqueued);
        // The transaction was begun (a real attempt was made) but never committed - the fake
        // transaction's own DisposeAsync is what a rolled-back Postgres transaction becomes here.
        Assert.Equal(1, fixture.UnitOfWork.TransactionsBegun);
        Assert.Equal(0, fixture.UnitOfWork.TransactionsCommitted);
    }

    /// <summary>`18-02`'s own instance of `6-10`'s shape: every attempt this handler is willing to make
    /// loses to write contention, so the caller gets a clean, visible refusal - never an unhandled
    /// exception, and never a false "it worked".</summary>
    [Fact]
    public async Task HandleAsync_WhenCapacityContentionPersistsOnEveryAttempt_ReturnsTransferContended()
    {
        var fixture = CreateHandlerWithAssignedConversation();
        fixture.Capacity.ClaimAlwaysLosesToContention = true;

        var result = await fixture.Handler.HandleAsync(Command(fixture), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.TransferContended", result.Error!.Value.Code);
        Assert.Equal(FromOperatorId, fixture.Conversation.OperatorId);
        Assert.Empty(fixture.Outbox.Enqueued);
        Assert.Equal(0, fixture.UnitOfWork.TransactionsCommitted);
        // Bounded at 5 attempts, matching OperatorCapacityStore.ReleaseAsync's own bound - see the
        // handler's own remarks on why a bare single retry (this item's first version) was revised
        // after measuring it fail under real contention.
        Assert.Equal(5, fixture.Capacity.Claims.Count);
    }

    /// <summary>The other half of the same shape: a transient contention that clears on the retry
    /// still lets the transfer through, exactly the "single transparent retry" `6-08` established.</summary>
    [Fact]
    public async Task HandleAsync_WhenCapacityContentionClearsOnRetry_Succeeds()
    {
        var fixture = CreateHandlerWithAssignedConversation();
        var attempt = 0;
        var flakyCapacity = new FlakyOnceCapacity(fixture.Capacity, () => attempt++ == 0);
        var handler = new Application.UseCases.TransferConversation.TransferConversationHandler(
            fixture.Conversations, fixture.Operators, fixture.Permissions, flakyCapacity, fixture.UnitOfWork,
            fixture.Outbox, new FakeIdGenerator(), new FakeClock(Now));

        var result = await handler.HandleAsync(Command(fixture), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Message : string.Empty);
        Assert.Equal(ToOperatorId, fixture.Conversation.OperatorId);
        Assert.Equal(1, fixture.UnitOfWork.TransactionsCommitted);
        // One transaction begun and abandoned, one begun and committed.
        Assert.Equal(2, fixture.UnitOfWork.TransactionsBegun);
    }

    /// <summary>Fails the very first <see cref="IOperatorCapacity.TryClaimAsync"/> call with
    /// <see cref="OperatorCapacityContentionException"/> and delegates every call after that to the
    /// real fake - the minimal seam needed to prove a transaction-level retry recovers from a single
    /// transient deadlock, without teaching <see cref="FakeOperatorCapacity"/> itself a call-counting
    /// mode it has no other use for.</summary>
    private sealed class FlakyOnceCapacity(
        Abstractions.IOperatorCapacity inner, Func<bool> shouldFailThisCall) : Abstractions.IOperatorCapacity
    {
        public Task<bool> TryClaimAsync(OperatorId operatorId, CancellationToken cancellationToken)
        {
            if (shouldFailThisCall())
            {
                return Task.FromException<bool>(
                    new Abstractions.OperatorCapacityContentionException(operatorId, attempts: 1, new InvalidOperationException("40P01")));
            }

            return inner.TryClaimAsync(operatorId, cancellationToken);
        }

        public Task ReleaseAsync(OperatorId operatorId, CancellationToken cancellationToken) =>
            inner.ReleaseAsync(operatorId, cancellationToken);
    }
}
