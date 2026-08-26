namespace Ago.Chat.Domain.Tests;

/// <summary>
/// `14-04`: <see cref="Conversation.AddSystemMessage"/> - the only way a
/// <see cref="MessageAuthorKind.System"/> message can exist, and therefore half of the offline
/// auto-reply's loop guard. The other half is
/// <c>SendOfflineAutoReplyHandler</c>'s refusal to act on anything but a visitor message; the two
/// are tested separately because either one alone is the whole guarantee's weak point.
/// </summary>
public class ConversationSystemMessageTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());

    private static Conversation StartConversation() =>
        Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);

    [Fact]
    public void AddSystemMessage_AuthorsItAsSystem_WithNoPrincipalBehindIt()
    {
        var conversation = StartConversation();

        var message = conversation.AddSystemMessage(
            new MessageId(Guid.NewGuid()), new MessageBody("We are closed."), Now);

        // The guard's structural half: nothing about this call can produce a Visitor-authored message,
        // because the caller has no say in the author kind at all.
        Assert.Equal(MessageAuthorKind.System, message.AuthorKind);
        Assert.Equal(Conversation.SystemAuthorId, message.AuthorId);
        Assert.Equal(Guid.Empty, message.AuthorId);
    }

    [Fact]
    public void AddSystemMessage_TakesTheNextSequence_AndRaisesMessageAdded()
    {
        var conversation = StartConversation();
        conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hello?"), Now);
        conversation.ClearDomainEvents();

        var message = conversation.AddSystemMessage(
            new MessageId(Guid.NewGuid()), new MessageBody("We are closed."), Now);

        Assert.Equal(2, message.Sequence);
        var raised = Assert.Single(conversation.DomainEvents.OfType<MessageAdded>());
        Assert.Equal(MessageAuthorKind.System, raised.AuthorKind);
    }

    [Fact]
    public void AddSystemMessage_WhileWaiting_IsAllowed()
    {
        var conversation = StartConversation();

        // The whole point: this is the state an unattended conversation is in.
        Assert.Equal(ConversationState.Waiting, conversation.State);
        conversation.AddSystemMessage(new MessageId(Guid.NewGuid()), new MessageBody("Closed."), Now);

        Assert.Single(conversation.Messages);
    }

    [Fact]
    public void AddSystemMessage_OnAClosedConversation_Throws()
    {
        var conversation = StartConversation();
        conversation.AssignTo(OperatorId, Now);
        conversation.Close(Now);

        Assert.Throws<InvalidConversationStateException>(() =>
            conversation.AddSystemMessage(new MessageId(Guid.NewGuid()), new MessageBody("Closed."), Now));
    }

    [Fact]
    public void IncrementUnreadCount_ForASystemMessage_CountsAgainstTheVisitor_NotTheOperator()
    {
        var conversation = StartConversation();
        var message = conversation.AddSystemMessage(
            new MessageId(Guid.NewGuid()), new MessageBody("Closed."), Now);

        conversation.IncrementUnreadCount(MessageAuthorKind.System, message.Sequence);

        Assert.Equal(1, conversation.VisitorUnreadCount);
        Assert.Equal(0, conversation.OperatorUnreadCount);
    }
}
