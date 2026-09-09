using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GrantModuleQuantity;

/// <summary>
/// `22-07`/`adr/0093`: "site X's module K now has quantity Q" - the calendar add-on's own "N masters"
/// is the first real caller, but this command carries no word of that (<see cref="ModuleQuantityGrant"/>'s
/// own remarks). Raw <see cref="ModuleKey"/> string in, the same shape
/// <c>EnableModuleForSite</c> already uses for its own module key.
///
/// <para>No tenant-facing endpoint exists for this yet - the identical, already-accepted gap
/// <c>EnableModuleForSite</c>'s own remarks name for itself ("no tenant-facing admin UI or endpoint
/// exists for this yet... an internal HTTP endpoint is optional/nice-to-have"). This command is
/// exercised directly by tests and is ready to sit behind one whenever the settings screen this
/// item's own backlog item describes is built.</para>
/// </summary>
/// <param name="RequestedBy">`17-01`'s tenant-scope rule: every use case that takes a
/// <see cref="SiteId"/> either checks <c>IPermissionChecker</c> or is listed as an argued exemption.
/// Granting a module's quantity is a site-configuration write, gated on
/// <see cref="Permission.SiteConfigure"/> - the same permission <c>EnableModuleForSite</c> already
/// gates the sibling "which module is enabled" write with.</param>
/// <param name="ExpectedAffectedCount">`23-88`: the write-time guard against a stale preview -
/// see <c>GrantModuleQuantityAsOwnerHandler</c>'s own remarks (the owner-facing sibling
/// handler) for the full reasoning, identical here.</param>
public sealed record GrantModuleQuantity(OperatorId RequestedBy, SiteId SiteId, string ModuleKey, int Quantity, int? ExpectedAffectedCount = null);
