namespace Ago.Chat.Application.UseCases.PublishPriceVersion;

/// <summary>
/// `25-43`: the platform owner publishes a new Rouble figure for <paramref name="Key"/>, effective the
/// moment this call commits. The identical shape
/// `Ago.Chat.Application.UseCases.PublishDocumentVersion.PublishDocumentVersion` already establishes
/// for `adr/0114`'s own mechanism - no version identifier arrives from the caller (server-minted,
/// `Domain.PublishedPriceVersion`'s own remarks), and this command carries no permission check of its
/// own: the entire access-control story is <c>OwnerPricingEndpoints</c>'s <c>RequirePlatformOwner</c>
/// gate, the same single-gate shape every other owner surface in this codebase already uses.
///
/// <para><b><paramref name="Key"/> is not validated against <see cref="Domain.PricedResourceKeys"/>
/// here - that is <c>PublishPriceVersionHandler</c>'s own job.</b> This record only carries the raw
/// string a caller sent; "is this a key code has actually registered" is exactly the check `25-43`'s
/// own first decision requires before a version is ever minted, so it belongs in the handler, not in
/// a command that exists only to carry input.</para>
/// </summary>
public sealed record PublishPriceVersion(string Key, decimal AmountRub);
