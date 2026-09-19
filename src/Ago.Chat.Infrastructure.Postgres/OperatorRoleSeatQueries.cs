using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `25-170`: the one composable `operator_roles`/`roles` join every "does this operator hold this role's
/// own seat" question in this codebase reduces to - a public entry point specifically so
/// <c>Ago.Chat.Worker</c>'s assignment claimers (<c>SkipLockedAssignmentClaimer</c>,
/// <c>RedisLockAssignmentClaimer</c>) can compose it into their own bulk candidate queries without ever
/// naming the internal <see cref="OperatorRoleRecord"/>/<see cref="RoleRecord"/> persistence types
/// themselves (`clean-architecture.md`'s own "a host may construct `AgoChatDbContext` directly, but
/// still never reaches past a persistence type it has no business naming" - `IUnitOfWork`'s own remarks
/// already carve out the first half of that exception for these exact two files; this is the narrowest
/// possible width for the second half, one queryable method, not a blanket `InternalsVisibleTo`).
/// <see cref="OperatorRoleRepository"/>'s own methods build on the identical join for the ordinary,
/// Application-facing questions (a single operator, a single site) - this is the bulk, composable form
/// the two claimers' own multi-predicate candidate queries need instead.
/// </summary>
public static class OperatorRoleSeatQueries
{
    /// <summary>Every operator id, across the whole deployment or narrowed by a caller's own further
    /// `Where`, who currently holds <paramref name="roleName"/>'s own seat on <paramref name="siteId"/> -
    /// an <see cref="IQueryable{T}"/>, not a materialized list, so a caller composes it into one larger
    /// query (<c>.Where(o =&gt; HeldSeatOperatorIds(...).Contains(o.Id))</c>) that EF translates into one
    /// round trip, never a second one.</summary>
    public static IQueryable<OperatorId> HeldSeatOperatorIds(AgoChatDbContext db, SiteId siteId, string roleName)
    {
        var roleIds = db.Roles.Where(r => r.SiteId == siteId && r.Name == roleName).Select(r => r.Id);
        return db.OperatorRoles.Where(or => or.HoldsSeat && roleIds.Contains(or.RoleId)).Select(or => or.OperatorId);
    }

    /// <summary>The role-agnostic form <see cref="Operator.CanSignIn"/> itself needs -"does this
    /// operator hold a seat on *any* role" - for a caller (<see cref="OperatorRepository.AnyOnlineForSiteAsync"/>)
    /// asking a site-wide, not a per-role, question: "is any staff member at all currently online",
    /// never narrowed to one role the way conversation-routing eligibility deliberately is
    /// (<see cref="HeldSeatOperatorIds"/>'s own `roleName` parameter). An Admin-role-only holder is
    /// "online staff" for that question even though they are not a routing candidate for either.</summary>
    public static IQueryable<OperatorId> HeldAnySeatOperatorIds(AgoChatDbContext db, SiteId siteId)
    {
        var roleIds = db.Roles.Where(r => r.SiteId == siteId).Select(r => r.Id);
        return db.OperatorRoles.Where(or => or.HoldsSeat && roleIds.Contains(or.RoleId)).Select(or => or.OperatorId);
    }
}
