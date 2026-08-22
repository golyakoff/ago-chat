namespace Ago.Chat.Domain;

/// <summary>
/// Raised by <see cref="Attachment.ConfirmReady"/> - `5-04`'s `AttachmentThumbnailConsumer` is the
/// first real subscriber (image attachments only; non-image types are ignored by that consumer, not
/// by this event). Mapped to the <c>AttachmentReady</c> integration event in
/// <c>Ago.Chat.Application/Mapping</c>, never serialized directly (`clean-architecture.md`).
/// <see cref="ObjectKey"/>/<see cref="ContentType"/> ride along so the consumer never has to reload
/// the attachment just to learn what to download.
/// </summary>
public sealed record AttachmentReady(
    AttachmentId AttachmentId,
    SiteId SiteId,
    ConversationId ConversationId,
    string ObjectKey,
    string ContentType,
    DateTimeOffset OccurredAt) : IDomainEvent;
