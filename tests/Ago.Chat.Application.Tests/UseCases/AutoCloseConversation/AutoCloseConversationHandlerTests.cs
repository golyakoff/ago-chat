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
        // `25-221`: a brand-new conversation starts Pending, not Waiting - graduate it with the
        // visitor's own real first message before AssignTo, which still only accepts Waiting.
        conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
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
    /// `25-118`: reverses `18-06`'s original scope note - see the handler's own remarks on why its
    /// guard narrowed from `!= Assigned` to `== Closed`. A `Waiting` conversation (never assigned - a
    /// conversation `AutoCloseInactiveConversationsQuery.FindStaleWidgetBatchIncludingWaitingAsync` can
    /// now genuinely surface, unlike the channel-kind/`FindStaleAssignedBatchAsync` scan, which still
    /// never selects one) is now closed successfully, with no capacity to release (the fails-before
    /// table's own second row: this is what "the new query variant actually reaches Waiting rows"
    /// means end to end, at the handler rather than the query).
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenTheConversationIsWaiting_ClosesItSuccessfully_WithNoCapacityToRelease()
    {
        var conversations = new FakeConversationRepository();
        var conversation = Ago.Chat.Domain.Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        // `25-221`: a genuinely Waiting conversation, not merely Pending - this test's own point is
        // "Waiting (never assigned)", which the visitor's own first real message is what reaches.
        conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        conversations.Seed(conversation);

        var outbox = new FakeOutboxWriter();
        var capacity = new FakeOperatorCapacity();
        var handler = new AutoCloseConversationHandler(
            conversations, capacity, outbox, new FakeIdGenerator(), new FakeClock(Now),
            NullLogger<AutoCloseConversationHandler>.Instance);

        var result = await handler.HandleAsync(
            new Ago.Chat.Application.UseCases.AutoCloseConversation.AutoCloseConversation(conversation.Id),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(ConversationState.Closed, conversation.State);
        Assert.Single(outbox.Enqueued);
        Assert.Empty(capacity.Releases);
    }

    /// <summary>`25-118`: the flip side of the test right above - a conversation already `Closed` is
    /// still refused, exactly as before the guard narrowed. Proves the guard's new `== Closed` form
    /// still catches the one case it always had to.</summary>
    [Fact]
    public async Task HandleAsync_WhenAlreadyClosed_FromWaiting_ReturnsInvalidState()
    {
        var conversations = new FakeConversationRepository();
        var conversation = Ago.Chat.Domain.Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        // `25-221`: a genuinely Waiting-then-Closed conversation, matching this test's own name.
        conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        conversation.Close(Now);
        conversation.ClearDomainEvents();
        conversations.Seed(conversation);

        var handler = new AutoCloseConversationHandler(
            conversations, new FakeOperatorCapacity(), new FakeOutboxWriter(), new FakeIdGenerator(), new FakeClock(Now),
            NullLogger<AutoCloseConversationHandler>.Instance);

        var result = await handler.HandleAsync(
            new Ago.Chat.Application.UseCases.AutoCloseConversation.AutoCloseConversation(conversation.Id),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.InvalidState", result.Error!.Value.Code);
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
