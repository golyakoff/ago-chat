using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.ListNonEntitledChannelCredentialsAsOwner;

/// <summary>
/// `23-85`/`adr/0151`: no parameters, spans every tenant - the identical "the resource is all sites"
/// shape <c>ListSuspensionsForOwner</c> already uses for its own unrestricted cross-tenant list. This
/// is the read half of the item's own hard requirement: "this is not authorised to switch on silently -
/// the author wants to walk through the whole flow end to end, personally, before it runs for real."
/// This query is that walkthrough's first step - a platform owner reviews exactly which accounts are
/// connected without an entitlement <em>before</em> anything is disconnected, never the other way
/// around.
/// </summary>
public sealed record ListNonEntitledChannelCredentialsAsOwner;

/// <summary>One active channel credential this deployment currently has no entitlement for - the row
/// <see cref="Application.UseCases.DisconnectNonEntitledChannelCredentialsAsOwner.DisconnectNonEntitledChannelCredentialsAsOwner"/>
/// expects to be handed back, by <see cref="ChannelCredentialId"/>, once the owner has actually looked
/// at this list.</summary>
public sealed record NonEntitledChannelCredentialSummary(
    ChannelCredentialId ChannelCredentialId, SiteId SiteId, ChannelKind Kind, DateTimeOffset CreatedAt);
