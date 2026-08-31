namespace Ago.Chat.Domain.Tests;

/// <summary>`14-09`: <see cref="EmailThreadState"/>'s own aggregate behaviour - <see cref="Start"/> anchors
/// the root, <see cref="EmailThreadState.RecordInbound"/> moves only the "last seen" pointer.</summary>
public class EmailThreadStateTests
{
    private static readonly ConversationId ConversationId = new(Guid.NewGuid());

    [Fact]
    public void Start_SetsRootAndLastInboundToTheSameFirstMessageId()
    {
        var thread = EmailThreadState.Start(ConversationId, "<first@visitor.example>", "Need help with my order");

        Assert.Equal("<first@visitor.example>", thread.RootMessageId);
        Assert.Equal("<first@visitor.example>", thread.LastInboundMessageId);
        Assert.Equal("Need help with my order", thread.Subject);
    }

    [Fact]
    public void RecordInbound_UpdatesLastInboundMessageId_ButNotRootMessageIdOrSubject()
    {
        var thread = EmailThreadState.Start(ConversationId, "<first@visitor.example>", "Need help with my order");

        thread.RecordInbound("<second@visitor.example>");

        Assert.Equal("<first@visitor.example>", thread.RootMessageId);
        Assert.Equal("<second@visitor.example>", thread.LastInboundMessageId);
        Assert.Equal("Need help with my order", thread.Subject);
    }

    /// <summary>A third (or Nth) inbound message keeps moving only the "last seen" pointer - proving
    /// <see cref="EmailThreadState.RecordInbound"/> is not a one-shot transition.</summary>
    [Fact]
    public void RecordInbound_CalledAgain_KeepsMovingOnlyLastInboundMessageId()
    {
        var thread = EmailThreadState.Start(ConversationId, "<first@visitor.example>", "Need help with my order");
        thread.RecordInbound("<second@visitor.example>");

        thread.RecordInbound("<third@visitor.example>");

        Assert.Equal("<first@visitor.example>", thread.RootMessageId);
        Assert.Equal("<third@visitor.example>", thread.LastInboundMessageId);
    }

    [Fact]
    public void ConversationId_IsWhatStartWasGiven()
    {
        var thread = EmailThreadState.Start(ConversationId, "<first@visitor.example>", "Subject");

        Assert.Equal(ConversationId, thread.ConversationId);
    }
}
