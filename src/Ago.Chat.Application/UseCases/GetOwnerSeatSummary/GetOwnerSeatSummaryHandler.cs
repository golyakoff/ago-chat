using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.OperatorRoleSeats;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetOwnerSeatSummary;

/// <summary>
/// `25-181`: the owner console's own "Пользователи" summary read - <see cref="GetSeatAssignmentSummary.GetSeatAssignmentSummaryHandler"/>'s
/// own per-role shape (`RoleSeatLimits.LimitFor`, `IOperatorRoleRepository.GetHeldSeatHolderIdsAsync`),
/// widened by exactly one additive term neither that read nor `RoleSeatLimits` itself knows about: the
/// platform owner's own live, hand-granted extra (<see cref="IOwnerSeatGrantStore.GetEffectiveExtraAsync"/>).
/// A platform-owner-only read, gated by `RequirePlatformOwner` on its own route rather than
/// <see cref="Application.Abstractions.IPermissionChecker"/> - the identical reason every other owner
/// surface in this codebase gives (<see cref="UseCases.SetUnconditionalModuleGrantAsOwner.SetUnconditionalModuleGrantAsOwnerHandler"/>'s
/// own remarks, unchanged): the platform owner has no <see cref="Permission"/> row this checker could
/// find, only the realm role behind that route policy.
/// </summary>
public sealed class GetOwnerSeatSummaryHandler(IOperatorRoleRepository operatorRoles, ISiteRepository sites, IOwnerSeatGrantStore grants, IClock clock)
{
    public async Task<Result<OwnerSeatSummaryDto>> HandleAsync(GetOwnerSeatSummary query, CancellationToken cancellationToken)
    {
        var site = await sites.GetByIdAsync(query.SiteId, cancellationToken);
        if (site is null)
        {
            return ConversationErrors.SiteNotFound(query.SiteId.Value);
        }

        var now = clock.UtcNow;

        var operatorsHeld = (await operatorRoles.GetHeldSeatHolderIdsAsync(
            query.SiteId, RoleSeatLimits.OperatorRoleName, cancellationToken)).Count;
        var operatorsExtra = await grants.GetEffectiveExtraAsync(
            query.SiteId, RoleSeatLimits.OwnerGrantRoleFor(RoleSeatLimits.OperatorRoleName), now, cancellationToken);
        var operatorsLimit = RoleSeatLimits.LimitFor(RoleSeatLimits.OperatorRoleName, site) + operatorsExtra;

        var administratorsHeld = (await operatorRoles.GetHeldSeatHolderIdsAsync(
            query.SiteId, RoleSeatLimits.AdminRoleName, cancellationToken)).Count;
        var administratorsExtra = await grants.GetEffectiveExtraAsync(
            query.SiteId, RoleSeatLimits.OwnerGrantRoleFor(RoleSeatLimits.AdminRoleName), now, cancellationToken);
        var administratorsLimit = RoleSeatLimits.LimitFor(RoleSeatLimits.AdminRoleName, site) + administratorsExtra;

        return new OwnerSeatSummaryDto(operatorsHeld, operatorsLimit, administratorsHeld, administratorsLimit);
    }
}
