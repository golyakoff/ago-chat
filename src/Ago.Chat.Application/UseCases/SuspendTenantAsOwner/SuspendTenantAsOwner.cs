using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.SuspendTenantAsOwner;

/// <summary>
/// `22-08`: the platform owner's own account-wide freeze - a suspected violation, never non-payment
/// (`adr/0073`'s downgrade already owns that case completely, `docs/backlog/22-08-*.md`'s own
/// *Answered* section). Deliberately carries no <see cref="OperatorId"/> - the platform owner has
/// none, the identical reason <c>RevokeModuleForSiteAsOwner</c> states for its own
/// <c>RevokedBy</c>.
/// </summary>
/// <param name="SiteId">The account to suspend.</param>
/// <param name="SuspendedBy">The platform owner's own Keycloak <c>sub</c> claim - recorded, never
/// authorising (<c>RequirePlatformOwner</c> on the route is the entire access-control story).</param>
/// <param name="DurationMinutes">How long, in minutes, from the moment this command runs -
/// <c>SuspendTenantAsOwnerHandler</c>'s own remarks state why there is no fixed system duration and
/// no invented upper bound.</param>
/// <param name="Reason">Required, non-blank - `22-08`'s own Scope: "the act, the actor, the reason and
/// the instant recorded... this is an act that later has to be justified to the person it was used
/// against."</param>
public sealed record SuspendTenantAsOwner(SiteId SiteId, string SuspendedBy, int DurationMinutes, string Reason);
