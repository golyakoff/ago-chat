using Ago.Chat.Domain;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>The join between an operator and a <see cref="RoleRecord"/> - an operator can hold more
/// than one role, even though Stage 1 only ever grants the single seeded <c>"Operator"</c> role.
///
/// <para><b>`25-170`: <see cref="HoldsSeat"/>/<see cref="GrantedAt"/> - the seat-holding and grant-time
/// facts moved here from <c>Domain.Operator</c>.</b> "Holds a seat" is a fact about this one
/// <c>(operator, role)</c> pairing, not about the operator account as a whole - the same founder can
/// hold a seat on one seeded role and not the other. See <c>Application.Abstractions.IOperatorRoleRepository</c>'s
/// own remarks for the full reasoning and every method that reads or writes these two columns.</para>
/// </summary>
internal sealed class OperatorRoleRecord
{
    public OperatorId OperatorId { get; set; }

    public Guid RoleId { get; set; }

    /// <summary>`25-170`: does this specific role assignment currently occupy one of the role's own
    /// paid seats. Defaults to <see langword="true"/> - every row this codebase writes today (bootstrap
    /// registration, invite redemption, a role change carrying the operator's existing seat status
    /// forward) already fits within its own role's capacity by construction, the identical "the correct
    /// starting state for every row, not a special case" reasoning <c>Domain.Operator.HoldsSeat</c>'s
    /// own pre-`25-170` remarks gave for itself.</summary>
    public bool HoldsSeat { get; set; } = true;

    /// <summary>`25-170`: when this role was granted to this operator - written at both places a role is
    /// ever assigned (<c>SiteRegistrationRepository</c>'s own founder seeding,
    /// <c>OperatorInviteRedemptionRepository</c>'s own invite redemption, and
    /// <c>ChangeOperatorRoleHandler</c>'s own role change via <c>IOperatorRoleRepository.ReplaceRoleAsync</c>).
    /// The reconciliation procedure's own tie-break input - "most recently granted loses first" - the
    /// identical `adr/0125` precedent <c>AdministratorLimitEnforcer</c>'s own retired tie-break already
    /// established for `role_change_records.changed_at`, now a real column on the row itself rather than
    /// derived from a second table that only partially covered it.</summary>
    public DateTimeOffset GrantedAt { get; set; }
}
