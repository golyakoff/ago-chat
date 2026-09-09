namespace Ago.Chat.Domain.Tests;

/// <summary>
/// `23-64`/`adr/0148`: <see cref="Conversation.AddAutoGreetingMessage"/> - the aggregate-level guard
/// that keeps the drawn greeting from ever becoming a second row, or a row on a conversation that
/// already has a real first message. This is the fails-before proof for the item's own Done-when
/// box "asserted by a test rather than by inspection": <see cref="AddAutoGreetingMessage_OnAConversationThatAlreadyHasAMessage_ReturnsNull"/>
/// fails the moment the `_messages.Count > 0` guard is removed from that method - see this file's own
/// commit message for the before/after run.
/// </summary>
public class ConversationAutoGreetingMessageTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());

    private static Conversation StartConversation() =>
        Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);

    [Fact]
    public void AddAutoGreetingMessage_OnABrandNewConversation_AuthorsItAsAutoGreeting_WithNoPrincipalBehindIt()
    {
        var conversation = StartConversation();

        var message = conversation.AddAutoGreetingMessage(
            new MessageId(Guid.NewGuid()), new MessageBody("Hi, need any help?"), Now);

        Assert.NotNull(message);
        Assert.Equal(MessageAuthorKind.AutoGreeting, message!.AuthorKind);
        // Not a fabricated operator - the same sentinel `AddSystemMessage` already uses, and for the
        // same reason: there is no real principal behind this message, and inventing one (a random or
        // arbitrary OperatorId) would be a lie the next reader of this column could not detect.
        Assert.Equal(Conversation.SystemAuthorId, message.AuthorId);
        Assert.Equal(Guid.Empty, message.AuthorId);
        Assert.Equal(1, message.Sequence);
    }

    [Fact]
    public void AddAutoGreetingMessage_OnABrandNewConversation_RaisesMessageAdded()
    {
        var conversation = StartConversation();

        var message = conversation.AddAutoGreetingMessage(
            new MessageId(Guid.NewGuid()), new MessageBody("Hi, need any help?"), Now);

        Assert.NotNull(message);
        var raised = Assert.Single(conversation.DomainEvents.OfType<MessageAdded>());
        Assert.Equal(MessageAuthorKind.AutoGreeting, raised.AuthorKind);
    }

    // The decisive guard: `MessageBatchWriter` calls this method speculatively, once per eligible
    // send, with no way to know in advance whether this conversation is still genuinely empty by the
    // time this runs (a retried Join/Send pair after a dropped connection is the real-world case).
    // This is the fails-before test named in this file's own class remarks.
    [Fact]
    public void AddAutoGreetingMessage_OnAConversationThatAlreadyHasAMessage_ReturnsNull()
    {
        var conversation = StartConversation();
        conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hello?"), Now);
        conversation.ClearDomainEvents();

        var message = conversation.AddAutoGreetingMessage(
            new MessageId(Guid.NewGuid()), new MessageBody("Hi, need any help?"), Now);

        Assert.Null(message);
        Assert.Empty(conversation.DomainEvents.OfType<MessageAdded>());
        // Only the one real message - the guard did not burn a sequence number or append a row.
        Assert.Single(conversation.Messages);
    }

    [Fact]
    public void AddAutoGreetingMessage_IsOnlyEverTheConversationsFirstMessage()
    {
        var conversation = StartConversation();

        var greeting = conversation.AddAutoGreetingMessage(
            new MessageId(Guid.NewGuid()), new MessageBody("Hi, need any help?"), Now);
        var visitorMessage = conversation.AddVisitorMessage(
            VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("Yes please"), Now);

        Assert.NotNull(greeting);
        Assert.Equal(1, greeting!.Sequence);
        Assert.Equal(2, visitorMessage.Sequence);
        Assert.Equal(2, conversation.Messages.Count);
        // The greeting reads first in the transcript, exactly as the backlog item requires -
        // "materialised retroactively as the conversation's first message."
        Assert.Same(conversation.Messages[0], greeting);
        Assert.Same(conversation.Messages[1], visitorMessage);
    }

    [Fact]
    public void AddAutoGreetingMessage_OnAClosedConversation_ReturnsNull_RatherThanThrowing()
    {
        var conversation = StartConversation();
        conversation.AssignTo(OperatorId, Now);
        conversation.Close(Now);

        // Unlike AddSystemMessage/AddVisitorMessage (which throw on a closed conversation), this
        // method returns null - MessageBatchWriter calls it speculatively and already treats "nothing
        // to materialise" as an ordinary outcome, not a caller error (this type's own remarks).
        var message = conversation.AddAutoGreetingMessage(
            new MessageId(Guid.NewGuid()), new MessageBody("Hi, need any help?"), Now);

        Assert.Null(message);
    }

    [Fact]
    public void IncrementUnreadCount_ForAnAutoGreetingMessage_CountsAgainstTheVisitor_NotTheOperator()
    {
        var conversation = StartConversation();
        var message = conversation.AddAutoGreetingMessage(
            new MessageId(Guid.NewGuid()), new MessageBody("Hi, need any help?"), Now);

        conversation.IncrementUnreadCount(MessageAuthorKind.AutoGreeting, message!.Sequence);

        Assert.Equal(1, conversation.VisitorUnreadCount);
        Assert.Equal(0, conversation.OperatorUnreadCount);
    }
}
