using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.UnlinkChannelIdentityAsOwner;

/// <summary>`14-12`/`adr/0079`: the platform owner's own unconditional unlink - see
/// <see cref="UnlinkChannelIdentityAsOwnerHandler"/>'s own remarks for why this is a wholly separate
/// command/handler from <c>UnlinkChannelIdentity</c> rather than a nullable-<c>OperatorId</c> branch on
/// it. Deliberately carries no <see cref="OperatorId"/> - the platform owner has none
/// (`authorization.md`'s own actor table: "no `operators` row, no `OperatorId`/`SiteId` claims").</summary>
public sealed record UnlinkChannelIdentityAsOwner(SiteId SiteId, ChannelIdentityId ChannelIdentityId);
