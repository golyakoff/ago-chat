namespace Ago.Chat.Contracts;

/// <summary>
/// The wire shape of `5-04`'s `AttachmentThumbnailConsumer` trigger. Named differently from
/// <c>Ago.Chat.Domain.AttachmentReady</c> on purpose - the same domain-event/contract naming split
/// <c>MessageAdded</c>/<c>MessageAccepted</c> and <c>ConversationAssigned</c>/
/// <c>ConversationAssignedToOperator</c> already established, so a mapper using both types side by
/// side never collides on a bare name. <see cref="ObjectKey"/>/<see cref="ContentType"/> ride along
/// so the consumer never has to reload the attachment just to learn what to download.
/// </summary>
public sealed record AttachmentConfirmed(
    Guid AttachmentId,
    DateTimeOffset OccurredAt,
    Guid SiteId,
    Guid CorrelationId,
    Guid ConversationId,
    string ObjectKey,
    string ContentType);
