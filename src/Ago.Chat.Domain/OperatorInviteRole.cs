namespace Ago.Chat.Domain;

/// <summary>
/// `26-241`: one role an <see cref="OperatorInvite"/> grants once redeemed. Before this item the invite
/// carried exactly one <c>RoleId</c>; an admin can now invite a colleague to more than one seeded role at
/// once (Operator + Admin), so the single column became a one-to-many child collection owned by the
/// invite aggregate - the identical `OperatorRoleRecord` shape the *redeemed* side of this join already
/// uses (`operator_roles`), mirrored on the *pending* side.
///
/// <para>Part of the <see cref="OperatorInvite"/> aggregate, never its own aggregate root - a role line
/// has no lifecycle independent of the invite that owns it (it is created with the invite and deleted
/// with it), the same "does this change independently, in its own transaction" test
/// <see cref="OperatorInvite"/>'s own remarks apply to justify the invite's separation from
/// <see cref="Site"/>, answered the opposite way here. A plain <see cref="Guid"/> role id, not a Domain
/// id type, matching <see cref="OperatorInvite.RoleIds"/> and <c>OperatorRoleRecord.RoleId</c> - roles
/// have no Domain model of their own yet (`OperatorInvite.RoleIds`' own remarks).</para>
/// </summary>
public sealed class OperatorInviteRole
{
    public OperatorInviteId OperatorInviteId { get; private set; }

    public Guid RoleId { get; private set; }

    internal OperatorInviteRole(OperatorInviteId operatorInviteId, Guid roleId)
    {
        OperatorInviteId = operatorInviteId;
        RoleId = roleId;
    }

    // EF Core materialization only (1-04's precedent) - never called by domain code.
    private OperatorInviteRole()
    {
    }
}
