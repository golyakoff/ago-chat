using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.AutoCloseConversation;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ago.Chat.Application.Tests.UseCases.AutoCloseConversation;

public class AutoCloseConversationHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        AutoCloseConversationHandler Handler,
        FakeConversationRepository Conversations,
        FakeOutboxWriter Outbox,
        FakeOperatorCapacity Capacity,
        Conversation Conversation);

    private static Fixture CreateHandlerWithAssignedConversation(bool holdsCapacityClaim = true)
    {
        var conversations = new FakeConversationRepository();
        var conversation = Ago.Chat.Domain.Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversation.AssignTo(OperatorId, Now, holdsCapacityClaim);
        // Simulates a fresh load from Postgres (EF's materialization ctor never raises domain events) -
        // the same reason CloseConversationHandlerTests clears them here too.
        conversation.ClearDomainEvents();
        conversations.Seed(conversation);

        var outbox = new FakeOutboxWriter();
        var capacity = new FakeOperatorCapacity();
        var handler = new AutoCloseConversationHandler(
            conversations, capacity, outbox, new FakeIdGenerator(), new FakeClock(Now),
            NullLogger<AutoCloseConversationHandler>.Instance);
        return new Fixture(handler, conversations, outbox, capacity, conversation);
    }

    [Fact]
    public async Task HandleAsync_WhenAssigned_ClosesAndStagesTheOutboxRow_WithNoPermissionCheckAtAll()
    {
        // Unlike CloseConversationHandler, there is no OperatorId on the command and no
        // IPermissionChecker in this handler's constructor at all - this test's own existence (it
        // never grants any permission) is part of what proves that.
        var fixture = CreateHandlerWithAssignedConversation();

        var result = await fixture.Handler.HandleAsync(
            new Ago.Chat.Application.UseCases.AutoCloseConversation.AutoCloseConversation(fixture.Conversation.Id),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(ConversationState.Closed, fixture.Conversation.State);

        var envelope = Assert.Single(fixture.Outbox.Enqueued);
        Assert.Equal(nameof(ConversationEnded), envelope.Type);
        Assert.Equal(fixture.Conversation.Id.Value, envelope.MessageId);
        Assert.Equal(fixture.Conversation.Id.Value.ToString(), envelope.PartitionKey);
    }

    [Fact]
    public async Task HandleAsync_WhenTheConversationHoldsACapacityClaim_ReleasesItForTheAssignedOperator()
    {
        var fixture = CreateHandlerWithAssignedConversation(holdsCapacityClaim: true);

        var result = await fixture.Handler.HandleAsync(
            new Ago.Chat.Application.UseCases.AutoCloseConversation.AutoCloseConversation(fixture.Conversation.Id),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(OperatorId, Assert.Single(fixture.Capacity.Releases));
        Assert.False(fixture.Conversation.HoldsCapacityClaim);
    }

    [Fact]
    public async Task HandleAsync_WhenTheConversationWasHandPicked_ReleasesNothing()
    {
        var fixture = CreateHandlerWithAssignedConversation(holdsCapacityClaim: false);

        var result = await fixture.Handler.HandleAsync(
            new Ago.Chat.Application.UseCases.AutoCloseConversation.AutoCloseConversation(fixture.Conversation.Id),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(ConversationState.Closed, fixture.Conversation.State);
        Assert.Empty(fixture.Capacity.Releases);
    }

    [Fact]
    public async Task HandleAsync_WhenTheCapacityReleaseLosesToContention_StillReportsTheCloseAsSuccessful()
    {
        var fixture = CreateHandlerWithAssignedConversation();
        fixture.Capacity.ReleaseAlwaysLosesToContention = true;

        var result = await fixture.Handler.HandleAsync(
            new Ago.Chat.Application.UseCases.AutoCloseConversation.AutoCloseConversation(fixture.Conversation.Id),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(ConversationState.Closed, fixture.Conversation.State);
        Assert.Equal([OperatorId], fixture.Capacity.Releases);
    }

    [Fact]
    public async Task HandleAsync_WhenAlreadyClosed_ReturnsInvalidState_AndReleasesNothingASecondTime()
    {
        var fixture = CreateHandlerWithAssignedConversation();
        var command = new Ago.Chat.Application.UseCases.AutoCloseConversation.AutoCloseConversation(fixture.Conversation.Id);
        Assert.True((await fixture.Handler.HandleAsync(command, CancellationToken.None)).IsSuccess);

        var result = await fixture.Handler.HandleAsync(command, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.InvalidState", result.Error!.Value.Code);
        Assert.Single(fixture.Capacity.Releases);
        Assert.Single(fixture.Outbox.Enqueued);
    }

    /// <summary>
    /// `18-06`'s own scope note, exercised directly: a `Waiting` conversation (never assigned - the
    /// query never selects one, but this proves the handler itself refuses one too, closing the race
    /// window between `AutoCloseInactiveConversationsQuery`'s scan and this handler actually running -
    /// see the handler's own remarks on why this check has to be explicit here, unlike
    /// `CloseConversationHandler`, which gets it for free from its `OperatorId` comparison.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenTheConversationIsWaiting_ReturnsInvalidState_AndTouchesNothing()
    {
        var conversations = new FakeConversationRepository();
        var conversation = Ago.Chat.Domain.Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversations.Seed(conversation);

        var outbox = new FakeOutboxWriter();
        var capacity = new FakeOperatorCapacity();
        var handler = new AutoCloseConversationHandler(
            conversations, capacity, outbox, new FakeIdGenerator(), new FakeClock(Now),
            NullLogger<AutoCloseConversationHandler>.Instance);

        var result = await handler.HandleAsync(
            new Ago.Chat.Application.UseCases.AutoCloseConversation.AutoCloseConversation(conversation.Id),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.InvalidState", result.Error!.Value.Code);
        Assert.Equal(ConversationState.Waiting, conversation.State);
        Assert.Empty(outbox.Enqueued);
        Assert.Empty(capacity.Releases);
    }

    [Fact]
    public async Task HandleAsync_WhenConversationDoesNotExist_ReturnsNotFound()
    {
        var conversations = new FakeConversationRepository();
        var handler = new AutoCloseConversationHandler(
            conversations, new FakeOperatorCapacity(), new FakeOutboxWriter(), new FakeIdGenerator(),
            new FakeClock(Now), NullLogger<AutoCloseConversationHandler>.Instance);

        var result = await handler.HandleAsync(
            new Ago.Chat.Application.UseCases.AutoCloseConversation.AutoCloseConversation(new ConversationId(Guid.NewGuid())),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }
}
