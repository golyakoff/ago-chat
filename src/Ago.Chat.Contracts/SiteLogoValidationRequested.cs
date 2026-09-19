namespace Ago.Chat.Contracts;

/// <summary>
/// `25-160`: the wire shape of the trigger `Ago.Chat.Worker`'s new logo-validating consumer subscribes
/// to - the sibling of <see cref="AttachmentConfirmed"/>. Named differently from
/// <c>Ago.Chat.Domain.SiteLogoUploadSubmitted</c> on purpose - the same domain-event/contract naming
/// split <see cref="AttachmentConfirmed"/>'s own remarks already establish for
/// <c>Ago.Chat.Domain.AttachmentReady</c>, so a mapper using both types side by side never collides on
/// a bare name and never needs a type alias to tell them apart (`CLAUDE.md`'s "rename the colliding
/// type" convention, not `using Alias = ...`).
/// </summary>
public sealed record SiteLogoValidationRequested(
    Guid MessageId, DateTimeOffset OccurredAt, Guid SiteId, Guid CorrelationId, string ObjectKey, string ContentType);
