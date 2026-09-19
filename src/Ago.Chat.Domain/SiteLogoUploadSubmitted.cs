namespace Ago.Chat.Domain;

/// <summary>
/// `25-160`: raised by <see cref="Site.SubmitLogoUpload"/> - the trigger for `Ago.Chat.Worker`'s new
/// validating consumer, the sibling of `5-04`'s `AttachmentConfirmed`/`AttachmentThumbnailConsumer` pair.
/// Maps to its own integration event (<c>Ago.Chat.Contracts.SiteLogoUploadSubmitted</c>), not
/// <c>SiteSettingsChanged</c> - nothing about a `pending` upload is a fact any cache-invalidation
/// consumer needs to react to (a `pending` status is not yet servable by anything), so folding it into
/// that contract would ask <c>SiteCacheInvalidationConsumer</c> to evict cache entries for a change
/// nobody has read yet, the same "raise the event a real consumer needs, not the one a sibling happens
/// to use" discipline <see cref="SiteOfflineAutoReplyUpdated"/>'s own remarks state for the opposite
/// case (a genuinely new event where an existing one would have under-described the change).
///
/// <para><see cref="ObjectKey"/> travels with the event for the identical reason
/// <c>Ago.Chat.Contracts.AttachmentConfirmed.ObjectKey</c> does: the one real consumer needs it to
/// download the pending object, and re-deriving it from <see cref="SiteId"/> would cost that consumer a
/// database round trip for a value the publisher already had in hand.</para>
/// </summary>
public sealed record SiteLogoUploadSubmitted(
    SiteId SiteId, string PublicKey, string ObjectKey, string ContentType, DateTimeOffset OccurredAt)
    : IDomainEvent;
