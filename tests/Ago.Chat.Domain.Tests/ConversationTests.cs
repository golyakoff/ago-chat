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

    [Fact]
    public void AssignTo_WhenClosed_ThrowsInvalidConversationStateException()
    {
        var conversation = StartConversation();
        conversation.Close(Now);

        Assert.Throws<InvalidConversationStateException>(() => conversation.AssignTo(OperatorId, Now));
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
    }

    public static TheoryData<Action<Conversation>> NonClosedStates() => new()
    {
        _ => { }, // still Waiting
        c => c.AssignTo(OperatorId, Now), // Assigned
    };

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
    public void IncrementUnreadCount_VisitorAuthored_IncrementsOperatorCountOnly()
    {
        var conversation = StartConversation();

        conversation.IncrementUnreadCount(MessageAuthorKind.Visitor);

        Assert.Equal(1, conversation.OperatorUnreadCount);
        Assert.Equal(0, conversation.VisitorUnreadCount);
    }

    [Fact]
    public void IncrementUnreadCount_OperatorAuthored_IncrementsVisitorCountOnly()
    {
        var conversation = StartConversation();

        conversation.IncrementUnreadCount(MessageAuthorKind.Operator);

        Assert.Equal(1, conversation.VisitorUnreadCount);
        Assert.Equal(0, conversation.OperatorUnreadCount);
    }

    [Fact]
    public void IncrementUnreadCount_CalledRepeatedly_Accumulates()
    {
        var conversation = StartConversation();

        conversation.IncrementUnreadCount(MessageAuthorKind.Visitor);
        conversation.IncrementUnreadCount(MessageAuthorKind.Visitor);
        conversation.IncrementUnreadCount(MessageAuthorKind.Visitor);

        Assert.Equal(3, conversation.OperatorUnreadCount);
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
