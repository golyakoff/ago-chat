using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.ListEnabledModulesForSite;

/// <summary>
/// `23-01`: closes a live cross-tenant read - <c>ModuleEndpoints.HandleGetAsync</c> used to call
/// <see cref="IEnabledModuleReadStore.GetForSiteAsync"/> directly with the route's <c>siteId</c>,
/// compared against nothing, so any authenticated operator of any site could list another tenant's
/// enabled modules (each module's <see cref="Domain.EnabledModule.EntryPoint"/>,
/// <see cref="Domain.EnabledModule.GrantedByOwner"/> and <see cref="Domain.EnabledModule.ExpiresAt"/>)
/// by naming its <c>siteId</c> in the route. This handler is the fix, in the shape this codebase
/// already uses for every other <c>/api/v1/sites/{siteId}/...</c> read -
/// <c>GetWidgetConfigHandler</c>, <c>GetOfflineAutoReplyHandler</c>, <c>GetBillingStatusHandler</c>,
/// <c>ListWebhookEndpointsHandler</c> - rather than the inline-in-the-endpoint check the four write
/// siblings on this route group were not given either: a handler is visible to
/// <c>Ago.Chat.Architecture.Tests.TenantScopeTests</c>, which walks every <c>*Handler</c>'s public
/// entry points and fails the build for one that takes a <see cref="Ago.Chat.Domain.SiteId"/> and
/// never calls <see cref="IPermissionChecker"/> - the inline shape would have stayed invisible to
/// that guard exactly as this handler's absence did.
///
/// <para><b>Gated on <see cref="Permission.SiteConfigure"/>, the same permission its four write
/// siblings already use</b> (<c>EnableModuleForSiteHandler</c>, <c>RevokeModuleForSiteHandler</c>,
/// <c>RotateModuleCredentialHandler</c>, <c>VerifyModuleRegistrationHandler</c>), not a new, narrower
/// permission. `authorization.md`'s own precedent for this route group already answers the "read vs.
/// write" question: `docs/architecture/authorization.md`'s section on `site:configure` states plainly
/// that a single site-level setting does not earn a permission of its own, and every other
/// <c>Get*Handler</c> on a <c>/sites/{siteId}/...</c> route reads under the identical permission that
/// guards writing it (<c>GetWidgetConfigHandler</c>/<c>UpdateWidgetConfigHandler</c>,
/// <c>GetOfflineAutoReplyHandler</c>/<c>UpdateOfflineAutoReplyHandler</c>) - nowhere in this codebase
/// does a site-scoped read use a permission its write sibling does not also require. A module's
/// <see cref="Domain.ModuleCredential"/> never rides along in <see cref="Abstractions.EnabledModuleSummary"/>'s
/// wire projection either way, so this read is not "merely" configuration-adjacent: it is the same
/// "who may see how this site is set up" question `site:configure` already answers everywhere else on
/// this route group, and inventing a narrower permission for it alone would be the one inconsistent
/// case.</para>
/// </summary>
public sealed class ListEnabledModulesForSiteHandler(
    IEnabledModuleReadStore moduleReadStore, IPermissionChecker permissions, IClock clock)
{
    public async Task<Result<IReadOnlyList<EnabledModuleSummary>>> HandleAsync(
        ListEnabledModulesForSite query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to view this site's enabled modules.");
        }

        var modules = await moduleReadStore.GetForSiteAsync(query.SiteId, clock.UtcNow, cancellationToken);
        // Explicit Result<T>.Success(...), not the implicit T -> Result<T> conversion every sibling
        // handler in this file's own remarks uses: C# never applies a user-defined conversion when
        // either side of it is an interface type, and IReadOnlyList<T> is one - `return modules;`
        // fails to compile here (CS0029) for exactly that reason, found building this change rather
        // than by inspection.
        return Result<IReadOnlyList<EnabledModuleSummary>>.Success(modules);
    }
}
