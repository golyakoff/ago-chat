namespace Ago.Chat.Domain;

/// <summary>
/// A single message within a <see cref="Conversation"/>. Constructed only through
/// <see cref="Conversation.AddVisitorMessage"/>/<see cref="Conversation.AddOperatorMessage"/> - the
/// internal constructor keeps every other assembly, including <c>Ago.Chat.Application</c>, from
/// creating one directly and bypassing the conversation's own invariants.
/// </summary>
public sealed class Message
{
    public MessageId Id { get; }

    public ConversationId ConversationId { get; }

    public int Sequence { get; }

    public MessageAuthorKind AuthorKind { get; }

    public Guid AuthorId { get; }

    public MessageBody Body { get; }

    /// <summary>`5-07`: the client-generated id realtime.md's Client protocol section named as a
    /// design intent since `3-03` and left unwired - see <see cref="Conversation.AddMessage"/> for
    /// where it is actually used (retry-dedup, checked against every message already in memory).
    /// <see langword="null"/> for a caller that never sent one (every pre-`5-07` client) - dedup is
    /// simply unavailable for that send, never a validation failure forced on an old caller.</summary>
    public Guid? ClientMessageId { get; }

    /// <summary>`5-03`: "message references the attachment, not the reverse" (`file-storage.md`) -
    /// this is that reference. Set once, at construction, alongside the same
    /// <c>Conversation.AddVisitorMessage</c>/<c>AddOperatorMessage</c> call that assigns
    /// <see cref="Sequence"/> - never mutated afterward, since a message's attachment is decided when
    /// the message is sent, not edited in later.</summary>
    public AttachmentId? AttachmentId { get; }

    public DateTimeOffset CreatedAt { get; }

    internal Message(
        MessageId id,
        ConversationId conversationId,
        int sequence,
        MessageAuthorKind authorKind,
        Guid authorId,
        MessageBody body,
        AttachmentId? attachmentId,
        Guid? clientMessageId,
        DateTimeOffset now)
    {
        Id = id;
        ConversationId = conversationId;
        Sequence = sequence;
        AuthorKind = authorKind;
        AuthorId = authorId;
        Body = body;
        AttachmentId = attachmentId;
        ClientMessageId = clientMessageId;
        CreatedAt = now;
    }

    // EF Core materialization only (1-04) - never called by domain code, not even by Conversation.
    private Message()
    {
    }
}
