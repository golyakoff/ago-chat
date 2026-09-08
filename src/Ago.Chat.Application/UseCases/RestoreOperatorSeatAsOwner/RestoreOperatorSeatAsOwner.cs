using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RestoreOperatorSeatAsOwner;

/// <summary>
/// `23-68`: the platform owner's own recovery write - restores one named operator's seat on one named
/// site, for the one case `23-71`'s own sign-in rule does not by itself cover: an operator who holds
/// no seat and holds no `Permission.SiteManageOperators` either, and so cannot sign in
/// (<see cref="Operator.CanSignIn"/>) and has nobody left on the tenant's own side who can toggle their
/// seat back on (`ToggleOperatorSeat`, gated on that same permission - the very thing the locked-out
/// tenant no longer has). See <see cref="RestoreOperatorSeatAsOwnerHandler"/>'s own remarks for why
/// this is a deliberately separate command/handler from <c>ToggleOperatorSeat</c> rather than a
/// nullable-<see cref="OperatorId"/> branch on it - the identical reasoning
/// <see cref="EnableModuleForSiteAsOwner.EnableModuleForSiteAsOwnerHandler"/>'s own remarks give for
/// its own owner surface.
///
/// <para><b>Deliberately restore-only, never a general toggle.</b> The backlog item's own Scope is "a
/// platform owner can restore an operator's seat", not "a platform owner can manage any operator's
/// seat state" - the narrower shape is what keeps this recovery action from quietly growing into the
/// wider "act as a tenant" capability the item's own "What this is not" explicitly forbids. Releasing a
/// seat from the owner console has no support scenario this item was filed for and is not built
/// here.</para>
///
/// <para><b><see cref="Force"/>/<see cref="Reason"/>: the seat-limit override, decided in this
/// change rather than discovered.</b> Restoring a seat is capacity-checked against the site's current
/// `seat_limit`, exactly as the tenant's own <c>ToggleOperatorSeat</c> is - but unlike that self-service
/// path, an owner may override the limit, because this call exists specifically for the incident case
/// where the ordinary self-service remedy ("upgrade, or free up a seat first") is unreachable: the
/// locked-out tenant cannot sign in to free anything up themselves. The asymmetry is the identical
/// shape <see cref="RevokeModuleForSiteAsOwner.RevokeModuleForSiteAsOwner"/>'s own <c>Force</c>/<c>Reason</c>
/// pair already establishes for a different override: the ordinary path (restoring within the limit)
/// carries no new ceremony; exceeding the limit is refused unless the caller states both a deliberate
/// <see langword="true"/> and a real justification, recorded verbatim
/// (<see cref="Abstractions.IOperatorSeatRestoreOverrideRepository"/>) - never silently exceeded, and
/// never permanently blocked either.</para>
///
/// <para><see cref="RestoredBy"/> is recorded, never authorising - the identical "the realm role behind
/// `RequirePlatformOwner` is the entire access-control story" shape
/// <see cref="RevokeModuleForSiteAsOwner.RevokeModuleForSiteAsOwner.RevokedBy"/>'s own remarks give for
/// itself. Deliberately carries no <see cref="OperatorId"/> naming the caller - the platform owner has
/// none (`adr/0032`).</para>
/// </summary>
/// <param name="SiteId">The tenant whose operator is being restored - named directly by the owner, the
/// identical "deliberate cross-tenant write" shape every owner command in this codebase already
/// carries.</param>
/// <param name="TargetOperatorId">The operator whose seat is being restored.</param>
/// <param name="RestoredBy">The platform owner's own Keycloak `sub` claim - recorded on the override
/// row when one is written, and on the <c>access_records</c> row `OwnerOperatorsEndpoints` writes for
/// every call regardless of whether an override was exercised.</param>
/// <param name="Force">Restoring within the site's current seat limit needs nothing more than this
/// defaulting to <see langword="false"/>. Restoring past the limit is refused unless this is
/// <see langword="true"/> and <see cref="Reason"/> is a real justification.</param>
/// <param name="Reason">Required whenever <see cref="Force"/> is set, checked before this handler
/// touches the operator or site row it is acting on - never optional-with-a-default. Free text,
/// recorded verbatim in <see cref="Abstractions.IOperatorSeatRestoreOverrideRepository"/>'s own row
/// only when the override is actually exercised (restoring stayed within the limit -> nothing is
/// written even if this was set).</param>
public sealed record RestoreOperatorSeatAsOwner(
    SiteId SiteId, OperatorId TargetOperatorId, string RestoredBy, bool Force = false, string? Reason = null);
