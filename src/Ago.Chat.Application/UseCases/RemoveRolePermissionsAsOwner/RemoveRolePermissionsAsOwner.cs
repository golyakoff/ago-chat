using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RemoveRolePermissionsAsOwner;

/// <summary>
/// `25-77`: the platform owner's own removal - the mirror `25-76`'s own text named as its own explicitly
/// carried-forward open question ("does v1 also need to remove a permission... `AddPermissionsAsync` has
/// no removal counterpart... building one is real, new work"). Wraps
/// <see cref="Application.Abstractions.IRoleRepository.RemovePermissionsAsync"/> - additive-idempotent in
/// reverse, outbox-published, now requiring a reason - the identical thin-wrapper shape
/// <see cref="AddRolePermissionsAsOwner.AddRolePermissionsAsOwner"/> already establishes for the grant
/// direction.
///
/// <para><b>No magic roles.</b> `docs/backlog/25-77-*.md`'s own "Answered, 2026-09-13": the owner may
/// remove any permission from any role, `Admin`'s own defining `site:configure`/`site:manage_operators`
/// included. No carve-out lives in this command, its handler, or the port it calls - the owner is
/// trusted, and a role is not a shape this tool protects on their behalf.</para>
/// </summary>
/// <param name="Permissions">One or more permission values to remove - refused as one unit if any
/// single value is not a real, known permission (see the handler's own remarks for why that check still
/// runs on the removal side, even though a value that was never real could only ever be a harmless
/// no-op). The identical "the whole request lands exactly as typed, or not at all" reasoning
/// <see cref="AddRolePermissionsAsOwner.AddRolePermissionsAsOwner"/>'s own remarks give for its own
/// list.</param>
/// <param name="Reason">Required, non-blank, on every call - `25-77`'s own "Answered": the identical
/// discipline `adr/0118`'s forced-revoke and `23-86`'s unconditional-grant flag already require for
/// taking something away that a tenant already had. Checked in
/// <see cref="RemoveRolePermissionsAsOwnerHandler"/> before anything else it does.</param>
public sealed record RemoveRolePermissionsAsOwner(
    SiteId SiteId, string RoleName, IReadOnlyList<string> Permissions, string RemovedBy, string Reason);
