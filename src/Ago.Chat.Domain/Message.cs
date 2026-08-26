namespace Ago.Chat.Domain;

/// <summary>
/// A single message within a <see cref="Conversation"/>. Constructed only through
/// <see cref="Conversation.AddVisitorMessage"/>/<see cref="Conversation.AddOperatorMessage"/>/<see
/// cref="Conversation.AddSystemMessage"/> - the internal constructor keeps every other assembly,
/// including <c>Ago.Chat.Application</c>, from creating one directly and bypassing the conversation's
/// own invariants.
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

    // `14-06`: three backing fields rather than one owned entity, mapped by name in
    // MessageConfiguration (the shape Site.WidgetConfig already uses) - an EF owned type would bring
    // nullable-owned-entity ceremony for a value that is absent on almost every row. Kept private so
    // the only way to read them is Content below, which cannot hand back a half-populated value.
    private MessageContentKind? _contentKind;
    private MessagePayload? _payload;
    private List<MessageAction>? _actions;

    /// <summary>`14-06`: the structured half of this message - a kind, an opaque payload and a list
    /// of actions - or <see langword="null"/> for the prose message that is still the overwhelming
    /// majority. AGO Chat validates the payload's *shape* and never its meaning: it holds no schema
    /// for it, which is what keeps another product's vocabulary out of this assembly (`adr/0061`).
    ///
    /// <para><see cref="Body"/> stays mandatory even when this is present, and that single rule is
    /// the whole rendering contract - a channel with no UI renders the body and numbers the actions,
    /// and never has to parse a payload it may not understand.</para>
    ///
    /// <para>Computed rather than stored, and <c>Ignore</c>d by EF, so the three columns are the one
    /// source of truth and cannot disagree with a fourth copy.</para></summary>
    public MessageContent? Content =>
        _contentKind is null ? null : MessageContent.Materialize(_contentKind.Value, _payload, _actions ?? []);

    internal Message(
        MessageId id,
        ConversationId conversationId,
        int sequence,
        MessageAuthorKind authorKind,
        Guid authorId,
        MessageBody body,
        AttachmentId? attachmentId,
        Guid? clientMessageId,
        MessageContent? content,
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

        if (content is not null)
        {
            _contentKind = content.Kind;
            _payload = content.Payload;
            _actions = [.. content.Actions];
        }
    }

    // EF Core materialization only (1-04) - never called by domain code, not even by Conversation.
    private Message()
    {
    }
}
