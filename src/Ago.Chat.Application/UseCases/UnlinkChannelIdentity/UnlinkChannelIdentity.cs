using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.UnlinkChannelIdentity;

/// <summary>`14-12`/`adr/0079`: an operator, holding the tenant-granted <see cref="Permission.ChannelIdentityUnlink"/>,
/// undoes a mistaken or stale link. See <see cref="UnlinkChannelIdentityHandler"/>'s own remarks, and
/// <c>UnlinkChannelIdentityAsOwner</c> for the platform owner's separate, permission-free path.</summary>
public sealed record UnlinkChannelIdentity(OperatorId RequestedBy, SiteId SiteId, ChannelIdentityId ChannelIdentityId);
