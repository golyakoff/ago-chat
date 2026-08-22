namespace Ago.Chat.Domain;

/// <summary>
/// `5-03`: its own aggregate root, not part of <see cref="Conversation"/> - it is created and later
/// confirmed in its own transaction, never inside the message-write transaction
/// (<c>Infrastructure.Postgres.Pipeline.MessageBatchWriter</c>, `4-05`), so bundling it into
/// <see cref="Conversation"/>'s aggregate boundary would violate `data-model.md`'s "one aggregate per
/// transaction" rule the moment a HEAD-verify confirm needed to save without touching the
/// conversation at all.
///
/// <see cref="ObjectKey"/> is a plain <see cref="string"/>, not <c>Ago.Platform.Abstractions.ObjectKey</c>
/// - the dependency rule (`clean-architecture.md`) allows Domain nothing but <c>Ago.Platform.Kernel</c>,
/// and <c>Ago.Platform.Abstractions.ObjectKey</c> lives one package over. The conversion happens in
/// Application, the one layer allowed to know both this type and the platform's file-storage port.
///
/// <see cref="MessageId"/> is the denormalized half of `file-storage.md`'s "message references the
/// attachment, not the reverse": the message's own <see cref="Domain.Message.AttachmentId"/> is the
/// real pointer a reader follows, this field only exists so `5-04`'s orphan sweep can ask "which
/// pending/ready attachments were never linked to a message" with a plain
/// <c>WHERE message_id IS NULL</c> instead of an anti-join against `messages`.
/// </summary>
public sealed class Attachment
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public AttachmentId Id { get; }

    public SiteId SiteId { get; }

    public ConversationId ConversationId { get; }

    public MessageId? MessageId { get; private set; }

    public string ObjectKey { get; } = string.Empty;

    public string ContentType { get; } = string.Empty;

    public long SizeBytes { get; }

    public AttachmentState State { get; private set; }

    /// <summary>Reserved by `5-03`'s own schema, populated by nobody yet - `5-04`'s async thumbnail
    /// job is the only intended writer. Nullable and unused today rather than added in a second
    /// migration alongside `5-04`, matching `MessageId`'s own "the column exists before its writer
    /// does" shape.</summary>
    public string? ThumbnailKey { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>`5-03` had no consumer for this yet, so <see cref="Attachment"/> raised nothing;
    /// `5-04`'s thumbnail consumer is the first real one - same "no domain-event plumbing ahead of a
    /// real subscriber" discipline as `ConfirmReady`'s own original remarks.</summary>
    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    private Attachment(
        AttachmentId id,
        SiteId siteId,
        ConversationId conversationId,
        string objectKey,
        string contentType,
        long sizeBytes,
        DateTimeOffset now)
    {
        Id = id;
        SiteId = siteId;
        ConversationId = conversationId;
        ObjectKey = objectKey;
        ContentType = contentType;
        SizeBytes = sizeBytes;
        State = AttachmentState.Pending;
        CreatedAt = now;
    }

    // EF Core materialization only (1-04's precedent) - never called by domain code.
    private Attachment()
    {
    }

    public static Attachment CreatePending(
        AttachmentId id,
        SiteId siteId,
        ConversationId conversationId,
        string objectKey,
        string declaredContentType,
        long declaredSizeBytes,
        DateTimeOffset now) =>
        new(id, siteId, conversationId, objectKey, declaredContentType, declaredSizeBytes, now);

    /// <summary>
    /// The client's own "I uploaded it" claim is never trusted (`file-storage.md`) - the caller
    /// passes what the platform's file-storage port actually found on the object via a HEAD-verify a
    /// moment earlier, and this method is the only place that decides whether that matches what was
    /// declared at presign time. A mismatch throws and leaves the attachment in
    /// <see cref="AttachmentState.Pending"/> - `5-03`'s Done-when is explicit that this must stay
    /// retryable, not become a dead row.
    /// </summary>
    public void ConfirmReady(long verifiedSizeBytes, string verifiedContentType, DateTimeOffset now)
    {
        if (State != AttachmentState.Pending)
        {
            throw new InvalidAttachmentStateException(
                $"Cannot confirm attachment {Id.Value} from state {State}; only {AttachmentState.Pending} can be confirmed.");
        }

        if (verifiedSizeBytes != SizeBytes || !string.Equals(verifiedContentType, ContentType, StringComparison.Ordinal))
        {
            throw new AttachmentVerificationMismatchException(
                $"Attachment {Id.Value}: declared {SizeBytes} bytes/{ContentType}, uploaded object is " +
                $"{verifiedSizeBytes} bytes/{verifiedContentType}.");
        }

        State = AttachmentState.Ready;
        _domainEvents.Add(new AttachmentReady(Id, SiteId, ConversationId, ObjectKey, ContentType, now));
    }

    /// <summary>
    /// Called from <c>MessageBatchWriter</c> once a message actually references this attachment,
    /// inside that same transaction - the only place `5-03` links the two. Rejects anything but a
    /// fresh <see cref="AttachmentState.Ready"/>, unlinked attachment belonging to the conversation
    /// the message is being added to: not-ready, already-linked, and wrong-conversation are exactly
    /// the three failure cases `5-03`'s Done-when names for this step.
    /// </summary>
    public void LinkToMessage(MessageId messageId, ConversationId conversationId)
    {
        if (ConversationId != conversationId)
        {
            throw new InvalidAttachmentStateException(
                $"Attachment {Id.Value} belongs to conversation {ConversationId.Value}, not {conversationId.Value}.");
        }

        if (State != AttachmentState.Ready)
        {
            throw new InvalidAttachmentStateException(
                $"Cannot reference attachment {Id.Value} from state {State}; only {AttachmentState.Ready} can be referenced.");
        }

        if (MessageId is not null)
        {
            throw new InvalidAttachmentStateException($"Attachment {Id.Value} is already referenced by a message.");
        }

        MessageId = messageId;
    }

    /// <summary>
    /// `5-03`'s own Done-when requires a message be rejected from referencing a `Deleted` attachment,
    /// so the state must be reachable even though `5-03` wires no real caller for it - `5-04`'s
    /// orphan sweep is the only intended one. Terminal: unlike <see cref="ConfirmReady"/>, there is no
    /// path back.
    /// </summary>
    public void MarkDeleted()
    {
        if (State == AttachmentState.Deleted)
        {
            throw new InvalidAttachmentStateException($"Attachment {Id.Value} is already deleted.");
        }

        State = AttachmentState.Deleted;
    }

    /// <summary>
    /// `5-04`: the only writer of `thumbnail_key`, reserved by `5-03`'s own schema. The consumer's
    /// own idempotency check (`AttachmentThumbnailGenerator`: skip if `ThumbnailKey` is already set)
    /// is what actually prevents a redelivered `AttachmentReady` from generating a second thumbnail -
    /// this method only guards the invariant a caller that skipped that check would otherwise violate.
    /// </summary>
    public void SetThumbnail(string thumbnailKey)
    {
        if (State != AttachmentState.Ready)
        {
            throw new InvalidAttachmentStateException(
                $"Cannot set a thumbnail for attachment {Id.Value} in state {State}; only {AttachmentState.Ready} accepts one.");
        }

        if (ThumbnailKey is not null)
        {
            throw new InvalidAttachmentStateException($"Attachment {Id.Value} already has a thumbnail.");
        }

        ThumbnailKey = thumbnailKey;
    }

    public void ClearDomainEvents() => _domainEvents.Clear();
}
