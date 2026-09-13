using Ago.Chat.Domain;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `25-77`: one row per exercised removal - the platform owner taking a permission away from a
/// tenant's role, "no magic roles" included (`docs/backlog/25-77-*.md`'s own "Answered": any
/// permission, `Admin`'s own defining ones included, may be removed). Written by
/// <see cref="RoleRepository.RemovePermissionsAsync"/> itself, inside the identical transaction as the
/// `roles` `UPDATE` and the `RoleAssignmentsChanged` outbox publish - a genuine improvement on
/// <c>ModuleRevokeOverrideEntity</c>'s own precedent (a second, raw-Npgsql connection, written after
/// the domain write commits): <see cref="RoleRepository"/> already holds an open EF transaction for
/// the permission change itself, so this row rides inside it rather than risking a removal that
/// commits with no reason recorded behind it, or a reason recorded for a removal that never
/// committed.
///
/// <para><b>No FK on <see cref="SiteId"/>, deliberately - the same `adr/0111`/`adr/0112`/`adr/0113`
/// mechanism <c>ModuleRevokeOverrideEntity</c>'s own remarks state in full.</b> A tenant whose role was
/// stripped of a permission and who later closes their account is exactly the tenant most likely to
/// ask, later, "who took this away and why" - a cascading foreign key would erase the answer with the
/// account.</para>
///
/// <para><b>No FK on <see cref="RoleName"/> either, even though (unlike <c>module_revoke_overrides</c>'s
/// own <c>module_key</c>) the `roles` row this describes is never deleted.</b> The identical
/// survive-the-erasure reasoning still applies: this record's own lifetime must not depend on the
/// `roles` row's, so <see cref="RoleName"/> is a snapshot of which role was named at removal time, not
/// a pointer to a row whose own lifecycle this table has no reason to be coupled to.</para>
/// </summary>
internal sealed class RolePermissionRemovalOverrideEntity
{
    public Guid Id { get; set; }

    public SiteId SiteId { get; set; }

    public string RoleName { get; set; } = string.Empty;

    /// <summary>Exactly the permissions this one call removed - never the role's resulting full set
    /// (that is <c>roles.permissions</c>' own job, read fresh, never duplicated here).</summary>
    public List<string> Permissions { get; set; } = [];

    public string RemovedBy { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public DateTimeOffset RemovedAt { get; set; }
}
