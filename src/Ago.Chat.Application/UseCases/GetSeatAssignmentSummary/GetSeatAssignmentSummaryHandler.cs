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
/// </summary>
public sealed class GetSeatAssignmentSummaryHandler(
    IOperatorRoleRepository operatorRoles, ISiteRepository sites, IPermissionChecker permissions)
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

        var roles = new List<RoleSeatAssignmentSummaryDto>(2);
        foreach (var roleName in new[] { RoleSeatLimits.OperatorRoleName, RoleSeatLimits.AdminRoleName })
        {
            var held = await operatorRoles.GetHeldSeatHolderIdsAsync(query.SiteId, roleName, cancellationToken);
            var limit = RoleSeatLimits.LimitFor(roleName, site);
            roles.Add(new RoleSeatAssignmentSummaryDto(roleName, held.Count, limit, held.Count > limit));
        }

        return new SeatAssignmentSummaryDto(roles);
    }
}
