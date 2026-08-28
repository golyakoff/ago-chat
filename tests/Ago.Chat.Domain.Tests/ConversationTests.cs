namespace Ago.Chat.Domain.Tests;

public class ConversationTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());

    private static Conversation StartConversation(DateTimeOffset? now = null) =>
        Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, now ?? Now);

    [Fact]
    public void Start_CreatesAWaitingConversation_AndRaisesConversationStarted()
    {
        var conversation = StartConversation();

        Assert.Equal(ConversationState.Waiting, conversation.State);
        Assert.Null(conversation.OperatorId);
        Assert.Equal(0, conversation.LastSequence);
        Assert.Empty(conversation.Messages);
        var raised = Assert.Single(conversation.DomainEvents);
        var started = Assert.IsType<ConversationStarted>(raised);
        Assert.Equal(conversation.Id, started.ConversationId);
        Assert.Equal(SiteId, started.SiteId);
        Assert.Equal(VisitorId, started.VisitorId);
    }

    [Fact]
    public void AssignTo_WhenWaiting_TransitionsToAssigned_AndRaisesConversationAssigned()
    {
        var conversation = StartConversation();

        conversation.AssignTo(OperatorId, Now);

        Assert.Equal(ConversationState.Assigned, conversation.State);
        Assert.Equal(OperatorId, conversation.OperatorId);
        Assert.Contains(conversation.DomainEvents, e => e is ConversationAssigned);
    }

    [Fact]
    public void AssignTo_WhenAlreadyAssigned_ThrowsInvalidConversationStateException()
    {
        var conversation = StartConversation();
        conversation.AssignTo(OperatorId, Now);

        Assert.Throws<InvalidConversationStateException>(() =>
            conversation.AssignTo(new OperatorId(Guid.NewGuid()), Now));
    }

    [Fact]
    public void AssignTo_WhenAlreadyAssignedToTheSameOperator_IsANoOp()
    {
        var conversation = StartConversation();
        conversation.AssignTo(OperatorId, Now);
        conversation.ClearDomainEvents();

        conversation.AssignTo(OperatorId, Now.AddMinutes(5));

        Assert.Equal(ConversationState.Assigned, conversation.State);
        Assert.Equal(OperatorId, conversation.OperatorId);
        Assert.Empty(conversation.DomainEvents);
    }

    /// <summary>`6-09`: the receipt for an engine-taken capacity slot - see
    /// <see cref="Conversation.HoldsCapacityClaim"/>. Default is no claim, which is the hand-picked
    /// path (<c>AssignConversationHandler</c>) and the safe direction.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AssignTo_RecordsWhetherTheAssignmentHoldsACapacityClaim(bool holdsCapacityClaim)
    {
        var conversation = StartConversation();

        conversation.AssignTo(OperatorId, Now, holdsCapacityClaim);

        Assert.Equal(holdsCapacityClaim, conversation.HoldsCapacityClaim);
    }

    [Fact]
    public void AssignTo_Default_TakesNoCapacityClaim()
    {
        var conversation = StartConversation();

        conversation.AssignTo(OperatorId, Now);

        Assert.False(conversation.HoldsCapacityClaim);
    }

    /// <summary>The reconnect no-op must not spend an engine claim: `3-03`'s repeat-join calls
    /// AssignTo again on every join, with the manual path's default of "no claim".</summary>
    [Fact]
    public void AssignTo_RepeatedBySameOperator_KeepsAnExistingCapacityClaim()
    {
        var conversation = StartConversation();
        conversation.AssignTo(OperatorId, Now, holdsCapacityClaim: true);

        conversation.AssignTo(OperatorId, Now.AddMinutes(5));

        Assert.True(conversation.HoldsCapacityClaim);
    }

    [Fact]
    public void Close_WhenTheAssignmentHoldsACapacityClaim_ConsumesItExactlyOnce()
    {
        var conversation = StartConversation();
        conversation.AssignTo(OperatorId, Now, holdsCapacityClaim: true);

        Assert.True(conversation.Close(Now));
        Assert.False(conversation.HoldsCapacityClaim);
        // The second close is rejected outright, so there is no interleaving in which the caller is
        // told to release twice for one conversation.
        Assert.Throws<InvalidConversationStateException>(() => conversation.Close(Now));
    }

    [Fact]
    public void Close_WhenTheAssignmentHoldsNoCapacityClaim_ConsumesNothing()
    {
        var conversation = StartConversation();
        conversation.AssignTo(OperatorId, Now);

        Assert.False(conversation.Close(Now));
    }

    [Fact]
    public void ReleaseToQueue_ConsumesTheCapacityClaimIfThereWasOne()
    {
        var claiming = StartConversation();
        claiming.AssignTo(OperatorId, Now, holdsCapacityClaim: true);
        Assert.True(claiming.ReleaseToQueue(Now));
        Assert.False(claiming.HoldsCapacityClaim);

        var handPicked = StartConversation();
        handPicked.AssignTo(OperatorId, Now);
        Assert.False(handPicked.ReleaseToQueue(Now));
    }

    [Fact]
    public void AssignTo_WhenClosed_ThrowsInvalidConversationStateException()
    {
        var conversation = StartConversation();
        conversation.Close(Now);

        Assert.Throws<InvalidConversationStateException>(() => conversation.AssignTo(OperatorId, Now));
    }

    [Fact]
    public void ReleaseToQueue_WhenAssigned_TransitionsToWaiting_ClearsOperatorId_AndRaisesConversationReleased()
    {
        var conversation = StartConversation();
        conversation.AssignTo(OperatorId, Now);
        conversation.ClearDomainEvents();

        conversation.ReleaseToQueue(Now.AddMinutes(5));

        Assert.Equal(ConversationState.Waiting, conversation.State);
        Assert.Null(conversation.OperatorId);
        var raised = Assert.Single(conversation.DomainEvents);
        var released = Assert.IsType<ConversationReleased>(raised);
        Assert.Equal(conversation.Id, released.ConversationId);
        Assert.Equal(OperatorId, released.PreviousOperatorId);
        Assert.Equal(Now.AddMinutes(5), released.OccurredAt);
    }

    [Fact]
    public void ReleaseToQueue_WhenWaiting_ThrowsInvalidConversationStateException()
    {
        var conversation = StartConversation();

        Assert.Throws<InvalidConversationStateException>(() => conversation.ReleaseToQueue(Now));
    }

    [Fact]
    public void ReleaseToQueue_WhenClosed_ThrowsInvalidConversationStateException()
    {
        var conversation = StartConversation();
        conversation.AssignTo(OperatorId, Now);
        conversation.Close(Now);

        Assert.Throws<InvalidConversationStateException>(() => conversation.ReleaseToQueue(Now));
    }

    [Fact]
    public void ReleaseToQueue_ThenAssignToADifferentOperator_Succeeds()
    {
        var conversation = StartConversation();
        conversation.AssignTo(OperatorId, Now);
        conversation.ReleaseToQueue(Now);
        var anotherOperator = new OperatorId(Guid.NewGuid());

        conversation.AssignTo(anotherOperator, Now);

        Assert.Equal(ConversationState.Assigned, conversation.State);
        Assert.Equal(anotherOperator, conversation.OperatorId);
    }

    [Theory]
    [MemberData(nameof(NonClosedStates))]
    public void Close_WhenNotAlreadyClosed_TransitionsToClosed_AndRaisesConversationClosed(
        Action<Conversation> arrange)
    {
        var conversation = StartConversation();
        arrange(conversation);

        conversation.Close(Now);

        Assert.Equal(ConversationState.Closed, conversation.State);
        Assert.Contains(conversation.DomainEvents, e => e is ConversationClosed);
        // `18-07`: the visitor-history summary's own timestamp - see Conversation.ClosedAt's remarks.
        Assert.Equal(Now, conversation.ClosedAt);
    }

    public static TheoryData<Action<Conversation>> NonClosedStates() => new()
    {
        _ => { }, // still Waiting
        c => c.AssignTo(OperatorId, Now), // Assigned
    };

    [Fact]
    public void ClosedAt_BeforeTheConversationIsEverClosed_IsNull()
    {
        var conversation = StartConversation();

        Assert.Null(conversation.ClosedAt);
    }

    [Fact]
    public void Close_WhenAlreadyClosed_ThrowsInvalidConversationStateException()
    {
        var conversation = StartConversation();
        conversation.Close(Now);

        Assert.Throws<InvalidConversationStateException>(() => conversation.Close(Now));
    }

    [Fact]
    public void AddVisitorMessage_WhenWaiting_Succeeds_AndAssignsSequenceOne()
    {
        var conversation = StartConversation();

        var message = conversation.AddVisitorMessage(
            VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hello"), Now);

        Assert.Equal(1, message.Sequence);
        Assert.Equal(1, conversation.LastSequence);
        Assert.Single(conversation.Messages);
        Assert.Equal(MessageAuthorKind.Visitor, message.AuthorKind);
    }

    [Fact]
    public void AddVisitorMessage_WhenAssigned_Succeeds()
    {
        var conversation = StartConversation();
        conversation.AssignTo(OperatorId, Now);

        var message = conversation.AddVisitorMessage(
            VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("still here"), Now);

        Assert.Equal(1, message.Sequence);
    }

    [Fact]
    public void AddVisitorMessage_WhenClosed_ThrowsInvalidConversationStateException()
    {
        var conversation = StartConversation();
        conversation.Close(Now);

        Assert.Throws<InvalidConversationStateException>(() =>
            conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("too late"), Now));
    }

    [Fact]
    public void AddVisitorMessage_WhenAuthorIsNotTheVisitor_ThrowsConversationParticipantMismatchException()
    {
        var conversation = StartConversation();
        var someoneElse = new VisitorId(Guid.NewGuid());

        Assert.Throws<ConversationParticipantMismatchException>(() =>
            conversation.AddVisitorMessage(someoneElse, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now));
    }

    [Fact]
    public void AddOperatorMessage_WhenAssignedToTheCorrectOperator_Succeeds()
    {
        var conversation = StartConversation();
        conversation.AssignTo(OperatorId, Now);

        var message = conversation.AddOperatorMessage(
            OperatorId, new MessageId(Guid.NewGuid()), new MessageBody("how can I help?"), Now);

        Assert.Equal(1, message.Sequence);
        Assert.Equal(MessageAuthorKind.Operator, message.AuthorKind);
    }

    [Fact]
    public void AddOperatorMessage_WhenWaiting_ThrowsInvalidConversationStateException()
    {
        var conversation = StartConversation();

        Assert.Throws<InvalidConversationStateException>(() =>
            conversation.AddOperatorMessage(OperatorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now));
    }

    [Fact]
    public void AddOperatorMessage_WhenClosed_ThrowsInvalidConversationStateException()
    {
        var conversation = StartConversation();
        conversation.AssignTo(OperatorId, Now);
        conversation.Close(Now);

        Assert.Throws<InvalidConversationStateException>(() =>
            conversation.AddOperatorMessage(OperatorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now));
    }

    [Fact]
    public void AddOperatorMessage_WhenAuthorIsNotTheAssignedOperator_ThrowsConversationParticipantMismatchException()
    {
        var conversation = StartConversation();
        conversation.AssignTo(OperatorId, Now);
        var someoneElse = new OperatorId(Guid.NewGuid());

        Assert.Throws<ConversationParticipantMismatchException>(() =>
            conversation.AddOperatorMessage(someoneElse, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now));
    }

    [Fact]
    public void Sequence_IncrementsAcrossVisitorAndOperatorMessages_RegardlessOfAuthor()
    {
        var conversation = StartConversation();
        conversation.AssignTo(OperatorId, Now);

        var first = conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        var second = conversation.AddOperatorMessage(OperatorId, new MessageId(Guid.NewGuid()), new MessageBody("hello"), Now);
        var third = conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("thanks"), Now);

        Assert.Equal([1, 2, 3], new[] { first.Sequence, second.Sequence, third.Sequence });
        Assert.Equal(3, conversation.LastSequence);
    }

    [Fact]
    public void AddVisitorMessage_RepeatedClientMessageId_ReturnsOriginalMessage_BurnsNoNewSequence()
    {
        // `5-07`: realtime.md's Client protocol section - "a retried send after a flaky reconnect
        // must not create a second message."
        var conversation = StartConversation();
        var clientMessageId = Guid.NewGuid();

        var first = conversation.AddVisitorMessage(
            VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now, clientMessageId: clientMessageId);
        var retry = conversation.AddVisitorMessage(
            VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now, clientMessageId: clientMessageId);

        Assert.Same(first, retry);
        Assert.Equal(1, conversation.LastSequence);
        Assert.Single(conversation.Messages);
    }

    [Fact]
    public void AddOperatorMessage_RepeatedClientMessageId_ReturnsOriginalMessage_BurnsNoNewSequence()
    {
        var conversation = StartConversation();
        conversation.AssignTo(OperatorId, Now);
        var clientMessageId = Guid.NewGuid();

        var first = conversation.AddOperatorMessage(
            OperatorId, new MessageId(Guid.NewGuid()), new MessageBody("hello"), Now, clientMessageId: clientMessageId);
        var retry = conversation.AddOperatorMessage(
            OperatorId, new MessageId(Guid.NewGuid()), new MessageBody("hello"), Now, clientMessageId: clientMessageId);

        Assert.Same(first, retry);
        Assert.Equal(1, conversation.LastSequence);
        Assert.Single(conversation.Messages);
    }

    [Fact]
    public void AddVisitorMessage_DifferentClientMessageIds_BothLand()
    {
        var conversation = StartConversation();

        var first = conversation.AddVisitorMessage(
            VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now, clientMessageId: Guid.NewGuid());
        var second = conversation.AddVisitorMessage(
            VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("again"), Now, clientMessageId: Guid.NewGuid());

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, conversation.LastSequence);
    }

    [Fact]
    public void AddVisitorMessage_NoClientMessageId_NeverDeduplicates()
    {
        // A caller that never sends one (every pre-`5-07` client) gets exactly the old behaviour -
        // no dedup check applies at all, matching realtime.md's own "null skips the check" contract.
        var conversation = StartConversation();

        var first = conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        var second = conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, conversation.LastSequence);
    }

    [Fact]
    public void IncrementUnreadCount_VisitorAuthored_IncrementsOperatorCountOnly()
    {
        var conversation = StartConversation();

        conversation.IncrementUnreadCount(MessageAuthorKind.Visitor, sequence: 1);

        Assert.Equal(1, conversation.OperatorUnreadCount);
        Assert.Equal(0, conversation.VisitorUnreadCount);
    }

    [Fact]
    public void IncrementUnreadCount_OperatorAuthored_IncrementsVisitorCountOnly()
    {
        var conversation = StartConversation();

        conversation.IncrementUnreadCount(MessageAuthorKind.Operator, sequence: 1);

        Assert.Equal(1, conversation.VisitorUnreadCount);
        Assert.Equal(0, conversation.OperatorUnreadCount);
    }

    [Fact]
    public void IncrementUnreadCount_CalledRepeatedly_Accumulates()
    {
        var conversation = StartConversation();

        conversation.IncrementUnreadCount(MessageAuthorKind.Visitor, sequence: 1);
        conversation.IncrementUnreadCount(MessageAuthorKind.Visitor, sequence: 2);
        conversation.IncrementUnreadCount(MessageAuthorKind.Visitor, sequence: 3);

        Assert.Equal(3, conversation.OperatorUnreadCount);
    }

    // `5-15` -------------------------------------------------------------------------------------

    /// <summary>An assigned conversation with <paramref name="visitorMessages"/> visitor messages,
    /// each already counted by `2-05`'s consumer - the ordinary state an operator finds one in.</summary>
    private static Conversation AssignedConversationWithUnread(int visitorMessages)
    {
        var conversation = StartConversation();
        conversation.AssignTo(OperatorId, Now);
        for (var i = 0; i < visitorMessages; i++)
        {
            var message = conversation.AddVisitorMessage(
                VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("incoming"), Now);
            conversation.IncrementUnreadCount(MessageAuthorKind.Visitor, message.Sequence);
        }

        conversation.ClearDomainEvents();
        return conversation;
    }

    [Fact]
    public void MarkReadByOperator_UpToTheNewestMessage_ClearsTheCountAndSetsTheWatermark()
    {
        var conversation = AssignedConversationWithUnread(3);

        var changed = conversation.MarkReadByOperator(OperatorId, upToSequence: 3);

        Assert.True(changed);
        Assert.Equal(0, conversation.OperatorUnreadCount);
        Assert.Equal(3, conversation.OperatorLastReadSequence);
    }

    [Fact]
    public void MarkReadByOperator_UpToAnOlderMessage_ClearsOnlyWhatWasSeen()
    {
        // The reason the operation takes a sequence at all: clearing is not "make it zero".
        var conversation = AssignedConversationWithUnread(3);

        conversation.MarkReadByOperator(OperatorId, upToSequence: 2);

        Assert.Equal(1, conversation.OperatorUnreadCount);
        Assert.Equal(2, conversation.OperatorLastReadSequence);
    }

    [Fact]
    public void MarkReadByOperator_AlreadyReadToThere_IsANoOpAndReportsNoChange()
    {
        // `5-15`'s idempotency requirement, at the level that can prove it means "no write at all"
        // rather than "a write that happens to be harmless" - the caller skips SaveAsync on false.
        var conversation = AssignedConversationWithUnread(2);
        conversation.MarkReadByOperator(OperatorId, upToSequence: 2);

        var changedAgain = conversation.MarkReadByOperator(OperatorId, upToSequence: 2);
        var changedBackwards = conversation.MarkReadByOperator(OperatorId, upToSequence: 1);

        Assert.False(changedAgain);
        Assert.False(changedBackwards);
        Assert.Equal(0, conversation.OperatorUnreadCount);
        Assert.Equal(2, conversation.OperatorLastReadSequence);
    }

    [Fact]
    public void MarkReadByOperator_ThenAMessageArrives_CountsItAgain()
    {
        // The whole point of the watermark: reading is not a permanent mute.
        var conversation = AssignedConversationWithUnread(2);
        conversation.MarkReadByOperator(OperatorId, upToSequence: 2);

        var arriving = conversation.AddVisitorMessage(
            VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("still there?"), Now);
        conversation.IncrementUnreadCount(MessageAuthorKind.Visitor, arriving.Sequence);

        Assert.Equal(1, conversation.OperatorUnreadCount);
    }

    [Fact]
    public void MarkReadByOperator_ThenTheIncrementForAnAlreadyReadMessageArrives_IsIgnored()
    {
        // The in-flight case: the message row commits, the operator reads it, and `2-05`'s consumer
        // only gets to it afterwards. Without the guard this would re-raise a count the operator has
        // already cleared, and the badge would light up for a message they are looking at.
        var conversation = StartConversation();
        conversation.AssignTo(OperatorId, Now);
        var message = conversation.AddVisitorMessage(
            VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);

        conversation.MarkReadByOperator(OperatorId, upToSequence: message.Sequence);
        conversation.IncrementUnreadCount(MessageAuthorKind.Visitor, message.Sequence);

        Assert.Equal(0, conversation.OperatorUnreadCount);
    }

    [Fact]
    public void MarkReadByOperator_ASequenceBeyondWhatExists_IsClampedToTheLastMessage()
    {
        // A client must not be able to mute messages that have not been written yet.
        var conversation = AssignedConversationWithUnread(2);

        conversation.MarkReadByOperator(OperatorId, upToSequence: 9999);

        Assert.Equal(2, conversation.OperatorLastReadSequence);

        var arriving = conversation.AddVisitorMessage(
            VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("later"), Now);
        conversation.IncrementUnreadCount(MessageAuthorKind.Visitor, arriving.Sequence);
        Assert.Equal(1, conversation.OperatorUnreadCount);
    }

    [Fact]
    public void MarkReadByOperator_OperatorMessagesInTheRange_DoNotSubtractFromTheOperatorsOwnCount()
    {
        var conversation = StartConversation();
        conversation.AssignTo(OperatorId, Now);
        var incoming = conversation.AddVisitorMessage(
            VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hello"), Now);
        conversation.IncrementUnreadCount(MessageAuthorKind.Visitor, incoming.Sequence);
        conversation.AddOperatorMessage(OperatorId, new MessageId(Guid.NewGuid()), new MessageBody("hi back"), Now);
        var second = conversation.AddVisitorMessage(
            VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("thanks"), Now);
        conversation.IncrementUnreadCount(MessageAuthorKind.Visitor, second.Sequence);

        conversation.MarkReadByOperator(OperatorId, upToSequence: 3);

        Assert.Equal(0, conversation.OperatorUnreadCount);
    }

    [Fact]
    public void MarkReadByOperator_WhenTheConsumerHasNotCaughtUp_NeverGoesNegative()
    {
        var conversation = StartConversation();
        conversation.AssignTo(OperatorId, Now);
        conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("a"), Now);
        conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("b"), Now);

        conversation.MarkReadByOperator(OperatorId, upToSequence: 2);

        Assert.Equal(0, conversation.OperatorUnreadCount);

        // And the increments that were in flight for those two land as no-ops, so the count stays put.
        conversation.IncrementUnreadCount(MessageAuthorKind.Visitor, sequence: 1);
        conversation.IncrementUnreadCount(MessageAuthorKind.Visitor, sequence: 2);
        Assert.Equal(0, conversation.OperatorUnreadCount);
    }

    [Fact]
    public void MarkReadByOperator_ByAnOperatorWhoIsNotAssigned_Throws()
    {
        var conversation = AssignedConversationWithUnread(1);

        Assert.Throws<ConversationParticipantMismatchException>(
            () => conversation.MarkReadByOperator(new OperatorId(Guid.NewGuid()), upToSequence: 1));
        Assert.Equal(1, conversation.OperatorUnreadCount);
    }

    [Fact]
    public void MarkReadByOperator_OnAWaitingConversationWithNoOperator_Throws()
    {
        var conversation = StartConversation();
        conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("anyone?"), Now);

        Assert.Throws<ConversationParticipantMismatchException>(
            () => conversation.MarkReadByOperator(OperatorId, upToSequence: 1));
    }

    [Fact]
    public void MarkReadByOperator_OnAClosedConversation_StillWorks()
    {
        // Closing is not a reason to keep nagging: the thread is still readable in the console, so
        // its badge must still be clearable. Deliberately no state check in the domain method.
        var conversation = AssignedConversationWithUnread(2);
        conversation.Close(Now);

        var changed = conversation.MarkReadByOperator(OperatorId, upToSequence: 2);

        Assert.True(changed);
        Assert.Equal(0, conversation.OperatorUnreadCount);
    }

    [Fact]
    public void MarkReadByOperator_DoesNotTouchTheVisitorsCount()
    {
        // `5-15`'s stated asymmetry, pinned by a test so it cannot drift silently: the visitor side
        // is deliberately unchanged, because nothing reads it yet.
        var conversation = StartConversation();
        conversation.AssignTo(OperatorId, Now);
        conversation.AddOperatorMessage(OperatorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        conversation.IncrementUnreadCount(MessageAuthorKind.Operator, sequence: 1);

        conversation.MarkReadByOperator(OperatorId, upToSequence: 1);

        Assert.Equal(1, conversation.VisitorUnreadCount);
    }

    /// <summary>
    /// `14-01`: CLAUDE.md rules 6 and 11 - "ordering never depends on a clock" - stated as a test
    /// rather than as a comment, because Stage 14 is the first time an *externally supplied* time
    /// gets anywhere near this system. Every channel provider stamps its deliveries, and a plausible
    /// mistake in `14-02`/`14-03` is to sort or backdate by that stamp. Here the second message is
    /// added with a timestamp an hour *earlier* than the first: the sequence still increments in call
    /// order, because <c>LastSequence</c> is what orders a conversation and <c>now</c> only ever
    /// records when a row was written.
    /// </summary>
    [Fact]
    public void Sequence_IncrementsInCallOrder_EvenWhenTheSuppliedTimeGoesBackwards()
    {
        var conversation = StartConversation();

        var first = conversation.AddVisitorMessage(
            VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("first"), Now);
        var second = conversation.AddVisitorMessage(
            VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("second"), Now.AddHours(-1));

        Assert.Equal([1, 2], new[] { first.Sequence, second.Sequence });
        Assert.True(second.CreatedAt < first.CreatedAt);
    }

    [Fact]
    public void ClearDomainEvents_RemovesEverythingRaisedSoFar()
    {
        var conversation = StartConversation();
        conversation.AssignTo(OperatorId, Now);

        conversation.ClearDomainEvents();

        Assert.Empty(conversation.DomainEvents);
    }
}
