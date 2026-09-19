namespace Ago.Chat.Application.Caching;

/// <summary>
/// `25-160`: the cached shape behind <see cref="SiteBrandingCacheKeys.ForLogo"/> - already base64, not
/// raw bytes, per this backlog item's own Design decisions: "computed once, not per email... a new
/// cache entry holds the already-base64-encoded payload." <see cref="ContentType"/> travels alongside
/// it because <c>EmailMimeMessageBuilder</c>'s own inline-image MIME part needs it and the validating
/// consumer that first computed both already has it in hand - re-deriving it from the object key's own
/// extension at read time would duplicate `Ago.Chat.Worker.SiteLogoValidator`'s own decode logic for no
/// reason.
///
/// A reference type (`record`, not `readonly record struct`) because <see cref="Ago.Platform.Abstractions.ICache"/>'s
/// own generic constraint requires one (<c>FakeCache</c>'s own remarks).
/// </summary>
public sealed record SiteBrandingLogoPayload(string Base64, string ContentType);
