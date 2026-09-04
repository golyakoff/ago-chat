using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.ListSitesForOwner;
using Ago.Chat.Contracts;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetSiteForOwner;

/// <summary>
/// `23-14`: assembles the platform owner's per-tenant detail read - decides the same recent-activity
/// window `ListSitesForOwnerHandler` decides, hands the named site to
/// <see cref="IPlatformOverviewReadStore.GetSiteAsync"/> and its enabled modules (history included) to
/// <see cref="IEnabledModuleReadStore.GetAllForSiteAsync"/>, and maps both to the wire shape.
/// Read-only, exactly like its sibling: opens no transaction, writes nothing, publishes nothing.
///
/// <para><b>This handler performs no authorization check, and that is deliberate rather than an
/// omission</b> - the identical reasoning <see cref="ListSitesForOwnerHandler"/>'s own remarks give in
/// full. The fact that authorizes this call is a `platform-owner` realm role Keycloak signs into the
/// token (`adr/0032`), decided once by `12-01`'s `RequirePlatformOwner` policy on
/// `GET /api/v1/owner/sites/{siteId}` (`OwnerSitesEndpoints`), the only route that resolves this
/// handler. `Ago.Chat.Application` has no port that can see a claim, so a second check here would be a
/// second, weaker copy of the same rule, free to drift from the first the moment either
/// changes.</para>
///
/// <para><b>Do not confuse this with `23-01`'s `ListEnabledModulesForSiteHandler`.</b> That handler
/// takes a `SiteId` <i>and</i> an `OperatorId` it checks through `IPermissionChecker` - a tenant's own
/// operator, reading their own site, refused for any other site by the ordinary RBAC path. This
/// handler takes only a `SiteId`, chosen by the caller, never checked against anything - the
/// deliberate cross-tenant sibling `tenant-isolation.md` lists in "the platform owner's" surfaces,
/// alongside `ListSitesForOwnerHandler` above it and the owner's three cross-tenant writes.
/// `TenantScopeExemptions` records this handler's entry point for exactly that reason: it takes a
/// `SiteId` and never calls `IPermissionChecker`, which is precisely the shape
/// `Ago.Chat.Architecture.Tests.TenantScopeTests` would otherwise fail the build over.</para>
///
/// <para><b>Returns a genuine "not found", not an info-hiding one.</b> Every tenant-scoped route in
/// this codebase makes "another tenant's row" indistinguishable from "no such row" - the right answer
/// when a caller could otherwise learn something about a tenant they cannot reach. That reasoning does
/// not apply here: the platform owner may legitimately name any site on the deployment, so a real
/// `Site.NotFound` is the honest answer to "this id does not exist", not a leak.</para>
/// </summary>
public sealed class GetSiteForOwnerHandler(
    IPlatformOverviewReadStore siteReadStore, IEnabledModuleReadStore moduleReadStore, IClock clock)
{
    public async Task<Result<OwnerSiteDetailResponse>> HandleAsync(
        GetSiteForOwner query, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        // The identical window ListSitesForOwnerHandler uses, so a site read from the list and the
        // same site read from its own detail route report the same recentMessageCount/lastMessageAt -
        // a caller drilling in from a search result must not see the numbers change underneath them.
        var recentSince = now.AddDays(-ListSitesForOwnerHandler.RecentWindowDays);

        var site = await siteReadStore.GetSiteAsync(query.SiteId, recentSince, cancellationToken);
        if (site is null)
        {
            return ConversationErrors.SiteNotFound(query.SiteId.Value);
        }

        var modules = await moduleReadStore.GetAllForSiteAsync(query.SiteId, now, cancellationToken);

        return new OwnerSiteDetailResponse(
            site.Id.Value,
            site.Name,
            ListSitesForOwnerHandler.OnlyTier,
            site.CreatedAt,
            site.SeatCount,
            site.ConversationCount,
            site.RecentMessageCount,
            site.LastMessageAt,
            site.AttachmentBytes,
            ListSitesForOwnerHandler.RecentWindowDays,
            modules.Select(ToModuleDto).ToList());
    }

    private static OwnerSiteModuleDto ToModuleDto(EnabledModuleDetailSummary module) => new(
        module.ModuleKey.Value,
        module.TriggerWords,
        module.EntryPoint.ToString(),
        module.GrantedByOwner,
        module.ExpiresAt,
        module.IsActive);
}
