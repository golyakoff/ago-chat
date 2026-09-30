using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.OperatorRoleSeats;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetSeatAssignmentSummary;

/// <summary>
/// `13-03`: a plain read across two independent counts (`IOperatorRoleRepository.GetHeldSeatHolderIdsAsync`,
/// `ISiteRepository.GetByIdAsync`'s own `SeatLimit`/`AdminLimit`) - no lock, no transaction. The
/// over-limit condition is exactly as fresh as the moment each of those reads ran, which is the correct
/// answer for a derived, read-time condition (this item's own Scope): a downgrade committing between the
/// reads changes what the *next* call to this handler reports, never what this one reports, and Postgres
/// MVCC guarantees each individual read is internally consistent - there is nothing here for a lock to
/// protect that a lock would not also have to hold across an entire console page render.
///
/// <para><b>`25-170`: one row per seeded role, not one Operator-role-only number.</b> The Admin role now
/// gets the identical over-limit visibility the Operator role always had - see
/// <see cref="RoleSeatAssignmentSummaryDto"/>'s own remarks.</para>
///
/// <para><b>`26-312`: each role's own limit now includes the platform owner's live grant on top of
/// whatever billing currently grants</b> (<see cref="RoleSeatLimits.LimitFor"/> alone, unmodified,
/// undercounted it whenever the owner had hand-granted an extra seat) - the identical additive term
/// <see cref="GetOwnerSeatSummary.GetOwnerSeatSummaryHandler"/> already applies for the owner console's
/// own read of the same two counts, read live rather than cached for the same reason that read is
/// (<see cref="IOwnerSeatGrantStore.GetEffectiveExtraAsync"/>'s own remarks, CLAUDE.md rule 8).</para>
/// </summary>
public sealed class GetSeatAssignmentSummaryHandler(
    IOperatorRoleRepository operatorRoles, ISiteRepository sites, IPermissionChecker permissions,
    IOwnerSeatGrantStore grants, IClock clock)
{
    public async Task<Result<SeatAssignmentSummaryDto>> HandleAsync(
        GetSeatAssignmentSummary query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.SiteManageOperators, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to manage this site's operators.");
        }

        var site = await sites.GetByIdAsync(query.SiteId, cancellationToken);
        if (site is null)
        {
            return ConversationErrors.SiteNotFound(query.SiteId.Value);
        }

        var now = clock.UtcNow;

        var roles = new List<RoleSeatAssignmentSummaryDto>(2);
        foreach (var roleName in new[] { RoleSeatLimits.OperatorRoleName, RoleSeatLimits.AdminRoleName })
        {
            var held = await operatorRoles.GetHeldSeatHolderIdsAsync(query.SiteId, roleName, cancellationToken);
            var extra = await grants.GetEffectiveExtraAsync(
                query.SiteId, RoleSeatLimits.OwnerGrantRoleFor(roleName), now, cancellationToken);
            var limit = RoleSeatLimits.LimitFor(roleName, site) + extra;
            roles.Add(new RoleSeatAssignmentSummaryDto(roleName, held.Count, limit, held.Count > limit));
        }

        return new SeatAssignmentSummaryDto(roles);
    }
}
