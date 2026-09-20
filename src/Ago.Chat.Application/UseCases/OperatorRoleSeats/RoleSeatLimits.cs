using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.OperatorRoleSeats;

/// <summary>
/// `25-170`: the one place that says which <see cref="Site"/> field limits which seeded role's own seat
/// count - "Operator -&gt; SeatLimit, Admin -&gt; AdminLimit" (this item's own design), shared by
/// <see cref="OperatorRoleSeatCapacity"/> and <see cref="OperatorRoleSeatReconciler"/> so the mapping is
/// never duplicated between the check that refuses a new seat and the sweep that disables an excess one.
/// The same bare role-name literals `ChangeOperatorRoleHandler`/`OperatorInviteRedemptionRepository`
/// already each declare their own copy of - no named-role catalogue exists yet for this codebase to
/// reach for instead (those types' own remarks).
/// </summary>
internal static class RoleSeatLimits
{
    public const string OperatorRoleName = "Operator";

    public const string AdminRoleName = "Admin";

    public static int LimitFor(string roleName, Site site) => roleName switch
    {
        OperatorRoleName => site.SeatLimit,
        AdminRoleName => site.AdminLimit,
        _ => throw new ArgumentOutOfRangeException(
            nameof(roleName), roleName, $"No seat limit is defined for role '{roleName}' - only '{OperatorRoleName}' and '{AdminRoleName}' carry one."),
    };

    /// <summary>`25-181`: which <see cref="OwnerSeatGrantRole"/> a seeded role name's own seat capacity
    /// corresponds to - the same pairing <see cref="LimitFor"/> already draws against <see cref="Site"/>'s
    /// two fields, restated for the owner's own hand-granted extra rather than a <see cref="Site"/>
    /// field.</summary>
    public static OwnerSeatGrantRole OwnerGrantRoleFor(string roleName) => roleName switch
    {
        OperatorRoleName => OwnerSeatGrantRole.Operator,
        AdminRoleName => OwnerSeatGrantRole.Administrator,
        _ => throw new ArgumentOutOfRangeException(
            nameof(roleName), roleName, $"No owner seat grant role is defined for role '{roleName}' - only '{OperatorRoleName}' and '{AdminRoleName}' carry one."),
    };
}
