using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetSuspensionStatusForSite;

/// <summary>
/// `25-70`: `docs/backlog/25-70-*.md`'s own missing half of `22-08` - the tenant's own console reading
/// its own account's suspension state, scoped to the caller's own site rather than every site
/// (`Ago.Chat.Application.UseCases.ListSuspensionsForOwner.ListSuspensionsForOwnerHandler`'s own
/// platform-owner-wide read is the sibling this is not).
///
/// <para><b>Reuses `22-08`'s own mechanism wholesale - no new port, no new table, no new migration.</b>
/// <see cref="ISiteSuspensionReadStore"/> already carries everything this handler needs
/// (<see cref="ISiteSuspensionReadStore.GetForTenantAsync"/>, added by this same change as one more
/// method on the existing interface, not a new abstraction - the identical "widen the interface, do not
/// invent a parallel one" choice <see cref="ISiteSuspensionReadStore.ListForOwnerAsync"/>'s own remarks
/// already made next to <see cref="ISiteSuspensionReadStore.ListActiveSuspensionsAsync"/>).</para>
///
/// <para><b>Gated on <see cref="Permission.SiteConfigure"/>, the identical permission and the identical
/// shape <see cref="ListEnabledModulesForSite.ListEnabledModulesForSiteHandler"/> already uses for the
/// other tenant-facing "how is my own site set up" read on the same
/// <c>/api/v1/sites/{siteId}/...</c> route group.</b> Not a new, narrower permission: a suspension's own
/// state is exactly the same "who may see how this site is set up" question that read already answers,
/// and (that handler's own remarks state this precedent in full) nowhere on this route group does a
/// site-scoped read use a permission its write sibling does not also require - there is no write sibling
/// here at all (this item is deliberately read-only for the tenant), so the read inherits the permission
/// the account's other configuration reads already use rather than inventing one for an action that does
/// not exist.</para>
///
/// <para><b>Visible to <c>Ago.Chat.Architecture.Tests.TenantScopeTests</c></b> - the same guard
/// <see cref="ListEnabledModulesForSite.ListEnabledModulesForSiteHandler"/>'s own remarks describe: this
/// handler takes a <see cref="SiteId"/> and calls <see cref="IPermissionChecker"/> in its own body, so
/// the architecture test that walks every <c>*Handler</c>'s entry points for exactly that shape passes
/// on this one without an exemption.</para>
/// </summary>
public sealed class GetSuspensionStatusForSiteHandler(
    ISiteSuspensionReadStore suspensions, IPermissionChecker permissions, IClock clock)
{
    public async Task<Result<TenantSuspensionStatus>> HandleAsync(
        GetSuspensionStatusForSite query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to view this site's suspension state.");
        }

        var status = await suspensions.GetForTenantAsync(query.SiteId, clock.UtcNow, cancellationToken);
        return Result<TenantSuspensionStatus>.Success(status);
    }
}
