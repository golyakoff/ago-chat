namespace Ago.Chat.Domain.Tests;

/// <summary>
/// `25-119`: <see cref="Message.MarkDelivered"/> - the widget's own delivery ack, recorded on the
/// message itself rather than a <see cref="ChannelDelivery"/>-shaped side table (that type's own
/// remarks explain why a widget visitor never has the <c>ChannelIdentityId</c>/<c>ChannelKind</c> it
/// requires). Two invariants this suite exists to prove: idempotent (a redelivered ack is a harmless
/// no-op, `adr/0020`), and scoped to an operator-authored message only.
/// </summary>
public class MessageDeliveredAtTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());

    private static Conversation StartAssignedConversation()
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversation.AssignTo(OperatorId, Now);
        return conversation;
    }

    [Fact]
    public void MarkDelivered_OnAnOperatorMessage_SetsDeliveredAt_AndReturnsTrue()
    {
        var conversation = StartAssignedConversation();
        var message = conversation.AddOperatorMessage(
            OperatorId, new MessageId(Guid.NewGuid()), new MessageBody("how can I help?"), Now);

        var applied = message.MarkDelivered(Now);

        Assert.True(applied);
        Assert.Equal(Now, message.DeliveredAt);
    }

    [Fact]
    public void MarkDelivered_BeforeAnyAck_LeavesDeliveredAtNull()
    {
        var conversation = StartAssignedConversation();
        var message = conversation.AddOperatorMessage(
            OperatorId, new MessageId(Guid.NewGuid()), new MessageBody("how can I help?"), Now);

        Assert.Null(message.DeliveredAt);
    }

    // `adr/0020`: a redelivered ack (the live push retried, or the widget's own fire-and-forget re-ack
    // on reconnect) must never move the timestamp, and must tell the caller nothing changed - that is
    // what lets AcknowledgeMessageDeliveredHandler decide not to publish a duplicate live push.
    [Fact]
    public void MarkDelivered_CalledTwice_IsIdempotent_KeepsTheFirstTimestamp_AndReturnsFalseTheSecondTime()
    {
        var conversation = StartAssignedConversation();
        var message = conversation.AddOperatorMessage(
            OperatorId, new MessageId(Guid.NewGuid()), new MessageBody("how can I help?"), Now);
        var firstAck = message.MarkDelivered(Now);

        var laterAck = Now.AddMinutes(5);
        var secondAck = message.MarkDelivered(laterAck);

        Assert.True(firstAck);
        Assert.False(secondAck);
        Assert.Equal(Now, message.DeliveredAt);
    }

    // `25-119`'s own scope: a visitor's own message has no "did the operator see it" concept this item
    // was asked to build - enforced on the mutator itself, not left to whichever call site reaches it.
    [Fact]
    public void MarkDelivered_OnAVisitorMessage_Throws()
    {
        var conversation = StartAssignedConversation();
        var message = conversation.AddVisitorMessage(
            VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hello?"), Now);

        Assert.Throws<InvalidOperationException>(() => message.MarkDelivered(Now));
    }

    [Fact]
    public void MarkDelivered_OnASystemMessage_Throws()
    {
        var conversation = StartAssignedConversation();
        var message = conversation.AddSystemMessage(new MessageId(Guid.NewGuid()), new MessageBody("we are closed."), Now);

        Assert.Throws<InvalidOperationException>(() => message.MarkDelivered(Now));
    }
}
