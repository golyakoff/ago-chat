using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.ReleaseInactiveConversation;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ago.Chat.Application.Tests.UseCases.ReleaseInactiveConversation;

/// <summary>`25-118`: mirrors `AutoCloseConversationHandlerTests`' own shape (same fakes, same
/// no-permission-check point, same capacity-conditional-release coverage), for the sibling handler
/// this item adds.</summary>
public class ReleaseInactiveConversationHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        ReleaseInactiveConversationHandler Handler,
        FakeConversationRepository Conversations,
        FakeConversationAssignmentLog AssignmentLog,
        FakeOutboxWriter Outbox,
        FakeOperatorCapacity Capacity,
        Conversation Conversation);

    private static Fixture CreateHandlerWithAssignedConversation(bool holdsCapacityClaim = true)
    {
        var conversations = new FakeConversationRepository();
        var conversation = Ago.Chat.Domain.Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversation.AssignTo(OperatorId, Now, holdsCapacityClaim);
        // Simulates a fresh load from Postgres (EF's materialization ctor never raises domain events) -
        // the same reason AutoCloseConversationHandlerTests clears them here too.
        conversation.ClearDomainEvents();
        conversations.Seed(conversation);

        var assignmentLog = new FakeConversationAssignmentLog();
        var outbox = new FakeOutboxWriter();
        var capacity = new FakeOperatorCapacity();
        var handler = new ReleaseInactiveConversationHandler(
            conversations, assignmentLog, capacity, outbox, new FakeIdGenerator(), new FakeClock(Now),
            NullLogger<ReleaseInactiveConversationHandler>.Instance);
        return new Fixture(handler, conversations, assignmentLog, outbox, capacity, conversation);
    }

    [Fact]
    public async Task HandleAsync_WhenAssigned_ReleasesToWaiting_AndStagesTheOutboxRow_WithNoPermissionCheckAtAll()
    {
        // Unlike CloseConversationHandler/AssignConversationHandler, there is no OperatorId on the
        // command and no IPermissionChecker in this handler's constructor at all - this test's own
        // existence (it never grants any permission) is part of what proves that.
        var fixture = CreateHandlerWithAssignedConversation();

        var result = await fixture.Handler.HandleAsync(
            new Ago.Chat.Application.UseCases.ReleaseInactiveConversation.ReleaseInactiveConversation(fixture.Conversation.Id),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(ConversationState.Waiting, fixture.Conversation.State);
        Assert.Null(fixture.Conversation.OperatorId);

        var envelope = Assert.Single(fixture.Outbox.Enqueued);
        Assert.Equal(nameof(ConversationReleasedToQueue), envelope.Type);
        Assert.Equal(fixture.Conversation.Id.Value.ToString(), envelope.PartitionKey);
    }

    /// <summary>`23-03`: the release closes the open assignment interval, the identical shape
    /// `OperatorConversationReleaser`'s own remarks describe for the operator-disconnect sweep this
    /// handler generalises to a single conversation.</summary>
    [Fact]
    public async Task HandleAsync_WhenAssigned_ClosesTheOpenAssignmentInterval()
    {
        var fixture = CreateHandlerWithAssignedConversation();

        await fixture.Handler.HandleAsync(
            new Ago.Chat.Application.UseCases.ReleaseInactiveConversation.ReleaseInactiveConversation(fixture.Conversation.Id),
            CancellationToken.None);

        Assert.Equal(fixture.Conversation.Id, Assert.Single(fixture.AssignmentLog.ClosedFor));
    }

    [Fact]
    public async Task HandleAsync_WhenTheConversationHoldsACapacityClaim_ReleasesItForTheAssignedOperator()
    {
        var fixture = CreateHandlerWithAssignedConversation(holdsCapacityClaim: true);

        var result = await fixture.Handler.HandleAsync(
            new Ago.Chat.Application.UseCases.ReleaseInactiveConversation.ReleaseInactiveConversation(fixture.Conversation.Id),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(OperatorId, Assert.Single(fixture.Capacity.Releases));
        Assert.False(fixture.Conversation.HoldsCapacityClaim);
    }

    [Fact]
    public async Task HandleAsync_WhenTheConversationWasHandPicked_ReleasesNoCapacity()
    {
        var fixture = CreateHandlerWithAssignedConversation(holdsCapacityClaim: false);

        var result = await fixture.Handler.HandleAsync(
            new Ago.Chat.Application.UseCases.ReleaseInactiveConversation.ReleaseInactiveConversation(fixture.Conversation.Id),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(ConversationState.Waiting, fixture.Conversation.State);
        Assert.Empty(fixture.Capacity.Releases);
    }

    [Fact]
    public async Task HandleAsync_WhenTheCapacityReleaseLosesToContention_StillReportsTheReleaseAsSuccessful()
    {
        var fixture = CreateHandlerWithAssignedConversation();
        fixture.Capacity.ReleaseAlwaysLosesToContention = true;

        var result = await fixture.Handler.HandleAsync(
            new Ago.Chat.Application.UseCases.ReleaseInactiveConversation.ReleaseInactiveConversation(fixture.Conversation.Id),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(ConversationState.Waiting, fixture.Conversation.State);
        Assert.Equal([OperatorId], fixture.Capacity.Releases);
    }

    /// <summary>The candidate scan (`AutoCloseInactiveConversationsQuery.FindStaleAssignedBatchAsync`)
    /// only ever selects `Assigned` rows for the widget bucket - but by the time this handler actually
    /// runs, the same defensive re-check `AutoCloseConversationHandler` makes for its own candidate
    /// applies here too: a message arrived, an operator closed it, or (once this ships) an earlier
    /// still-in-flight scope already released it. Not an error - the ordinary "skip, re-evaluate next
    /// cycle" outcome.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheConversationIsWaiting_ReturnsInvalidState_AndTouchesNothing()
    {
        var conversations = new FakeConversationRepository();
        var conversation = Ago.Chat.Domain.Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversations.Seed(conversation);

        var assignmentLog = new FakeConversationAssignmentLog();
        var outbox = new FakeOutboxWriter();
        var capacity = new FakeOperatorCapacity();
        var handler = new ReleaseInactiveConversationHandler(
            conversations, assignmentLog, capacity, outbox, new FakeIdGenerator(), new FakeClock(Now),
            NullLogger<ReleaseInactiveConversationHandler>.Instance);

        var result = await handler.HandleAsync(
            new Ago.Chat.Application.UseCases.ReleaseInactiveConversation.ReleaseInactiveConversation(conversation.Id),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.InvalidState", result.Error!.Value.Code);
        Assert.Equal(ConversationState.Waiting, conversation.State);
        Assert.Empty(outbox.Enqueued);
        Assert.Empty(capacity.Releases);
        Assert.Empty(assignmentLog.ClosedFor);
    }

    [Fact]
    public async Task HandleAsync_WhenAlreadyClosed_ReturnsInvalidState_AndReleasesNothing()
    {
        var conversations = new FakeConversationRepository();
        var conversation = Ago.Chat.Domain.Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversation.Close(Now);
        conversation.ClearDomainEvents();
        conversations.Seed(conversation);

        var handler = new ReleaseInactiveConversationHandler(
            conversations, new FakeConversationAssignmentLog(), new FakeOperatorCapacity(), new FakeOutboxWriter(),
            new FakeIdGenerator(), new FakeClock(Now), NullLogger<ReleaseInactiveConversationHandler>.Instance);

        var result = await handler.HandleAsync(
            new Ago.Chat.Application.UseCases.ReleaseInactiveConversation.ReleaseInactiveConversation(conversation.Id),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.InvalidState", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenConversationDoesNotExist_ReturnsNotFound()
    {
        var conversations = new FakeConversationRepository();
        var handler = new ReleaseInactiveConversationHandler(
            conversations, new FakeConversationAssignmentLog(), new FakeOperatorCapacity(), new FakeOutboxWriter(),
            new FakeIdGenerator(), new FakeClock(Now), NullLogger<ReleaseInactiveConversationHandler>.Instance);

        var result = await handler.HandleAsync(
            new Ago.Chat.Application.UseCases.ReleaseInactiveConversation.ReleaseInactiveConversation(new ConversationId(Guid.NewGuid())),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }
}
