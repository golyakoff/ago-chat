using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.AddRolePermissionsAsOwner;

/// <summary>
/// `25-76`: the platform owner's own write for "a tenant's role is missing a permission, add it" -
/// found live, twice in one evening (`docs/backlog/25-76-*.md`'s own "Found"): `RegisterSiteHandler`/
/// `MintDemoTenantHandler` write a site's roles once, at registration, with whatever permission list
/// that handler's source happens to name that day, and nothing afterward ever revisits an
/// already-created row - seven tenants queried live, seven different `Admin` permission sets, no two
/// identical.
///
/// <para><b>A thin wrapper, deliberately.</b> <see cref="Application.Abstractions.IRoleRepository.AddPermissionsAsync"/>
/// is already exactly the right shape - additive, idempotent, outbox-published
/// (`RoleAssignmentsChanged`, `23-102`/`23-104`) - so this handler's own job is choosing which
/// permissions reach it and refusing what should never: a permission string that is not a real, known
/// <see cref="Domain.Permission"/>. No new repository write exists for this direction, matching this
/// item's own scope decision.</para>
///
/// <para><b>ADD only - this item builds no removal path.</b> The item's own text names removal as a
/// real, separate open question ("a role losing a permission has to answer what happens to
/// `RoleAssignmentsChanged` and to any operator mid-session holding it") deliberately left to a future
/// item, not solved incidentally by this one.</para>
/// </summary>
/// <param name="Permissions">One or more permission values to add - every one checked against
/// <see cref="Domain.Permission.AllKnownValues"/> before anything is written, refused as one unit if
/// any single value is not real (partial success on this kind of call is not obviously a kindness: a
/// platform owner correcting a role wants to know the whole request landed exactly as typed, not that
/// three of four permissions it named quietly went through while the fourth was silently dropped).
/// </param>
public sealed record AddRolePermissionsAsOwner(SiteId SiteId, string RoleName, IReadOnlyList<string> Permissions);
