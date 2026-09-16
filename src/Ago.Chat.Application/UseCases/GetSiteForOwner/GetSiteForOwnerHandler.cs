using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.ListSitesForOwner;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
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
    IPlatformOverviewReadStore siteReadStore, IEnabledModuleReadStore moduleReadStore, ISiteRepository siteRepository,
    IModuleQuantityGrantStore quantityGrants, IOperatorTeamReadStore operatorTeam, IRoleRepository roles, IClock clock,
    IBillingOptionEntitlementProvider entitlements)
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

        // `23-48`: AllowedOrigins is not one of IPlatformOverviewReadStore's own read-model columns
        // (that port answers usage-signal questions, not "what is this site's own configuration") -
        // loaded straight off the write-side aggregate instead, the same single-row ISiteRepository
        // fetch EnableModuleForSiteAsOwnerHandler already makes for this site's display name. A low-
        // frequency, human-triggered admin read: no caching concern (`caching.md`'s own reasoning for
        // this whole port applies equally to one more field on it).
        var aggregate = await siteRepository.GetByIdAsync(query.SiteId, cancellationToken);
        var allowedOrigins = aggregate?.AllowedOrigins ?? [];

        // `23-66`/`25-115`: one read alongside the modules list above, not one per row - a site
        // enables a handful of modules at most, so this is a single extra query rather than an N+1.
        // The aggregate itself, not `GetAllForSiteAsync`'s own OR'd int: `25-115`'s channel-entitlement
        // table needs UnconditionallyGrantedByOwner/UnconditionalGrantExpiresAt to show provenance and
        // expiry, which the int-only read cannot carry - and building `Modules`' own quantity map off
        // this same list (below) means this stays the identical one extra query it always was, not two.
        var grants = await quantityGrants.GetGrantsForSiteAsync(query.SiteId, cancellationToken);
        var quantities = grants.ToDictionary(g => g.ModuleKey, g => g.EffectiveQuantity(now));

        // `23-68`: the identical read GetOperatorTeamHandler already serves a tenant's own team screen
        // (IOperatorTeamReadStore.GetForSiteAsync) - reused unchanged rather than a second query shape,
        // so the console can name a locked-out operator to restore a seat for.
        var operators = await operatorTeam.GetForSiteAsync(query.SiteId, cancellationToken);

        // `25-76`: this site's own roles, read fresh rather than assumed from whatever a new
        // registration would produce today - the whole point of this item's own "Found" (seven live
        // tenants, seven different `Admin` permission sets, because nothing before this item ever
        // re-read one).
        var roleRows = await roles.GetAllForSiteAsync(query.SiteId, cancellationToken);

        // `25-115`: one row per `ChannelKind` this deployment has actually priced - replacing
        // `25-114`'s single `ChannelQuantity: int?` field, delivered against a design (a numeric
        // quantity, Telegram only) that did not match what the author had described before `25-114`
        // shipped. Resolved through the identical `IBillingOptionEntitlementProvider` +
        // `ChannelEntitlementOptionKeys` pair `ChannelEntitlement.IsEntitledAsync` already uses to
        // decide whether a connect attempt is entitled - not a new, second-guessable literal
        // `ModuleKey` here (`ModuleKeyLiteralRule`'s own reason `"ai"` is deployment-configured, never
        // a literal in `Ago.Chat.*`, applies identically to this one). A kind this deployment has never
        // priced (`entitlements.TryGet` returns `null`) is omitted from the list entirely - the
        // console has nothing to offer a grant/revoke action against for a kind nobody sells.
        var channelEntitlements = Enum.GetValues<ChannelKind>()
            .Select(kind => (Kind: kind, ModuleKey: entitlements.TryGet(ChannelEntitlementOptionKeys.For(kind))))
            .Where(pair => pair.ModuleKey is not null)
            .Select(pair => ToChannelEntitlementDto(
                pair.Kind, pair.ModuleKey!.Value, grants.FirstOrDefault(g => g.ModuleKey == pair.ModuleKey!.Value), now))
            .ToList();

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
            modules.Select(module => ToModuleDto(module, quantities)).ToList(),
            allowedOrigins,
            operators.Select(ToOperatorDto).ToList(),
            aggregate?.SuspendedUntil,
            roleRows.Select(ToRoleDto).ToList(),
            Permission.AllKnownValues,
            channelEntitlements);
    }

    private static OwnerSiteOperatorDto ToOperatorDto(OperatorTeamMemberItem item) => new(
        item.OperatorId.Value, item.DisplayName, item.Email, item.HoldsSeat, item.RoleNames);

    private static OwnerSiteRoleDto ToRoleDto(RoleSummary role) => new(role.Name, role.Permissions);

    private static OwnerSiteModuleDto ToModuleDto(
        EnabledModuleDetailSummary module, IReadOnlyDictionary<ModuleKey, int> quantities) => new(
        module.Id.Value,
        module.ModuleKey.Value,
        module.TriggerWords,
        module.EntryPoint.ToString(),
        module.GrantedByOwner,
        module.ExpiresAt,
        module.RevokedAt,
        module.Status,
        quantities.TryGetValue(module.ModuleKey, out var quantity) ? quantity : null);

    /// <summary>`25-115`: one row of the owner's channel-entitlement table - <paramref name="grant"/>
    /// is <see langword="null"/> when this site has never had a grant row for this channel's own
    /// <see cref="ModuleKey"/> at all (never bought, never owner-granted), which reads identically to a
    /// grant row present with <see cref="ModuleQuantityGrant.Quantity"/> zero and no live unconditional
    /// flag - both are "not entitled right now", the same collapse
    /// <see cref="ModuleQuantityGrant.EffectiveQuantity"/> already makes for every other caller of this
    /// port, restated here rather than re-derived a second way.</summary>
    private static OwnerSiteChannelEntitlementDto ToChannelEntitlementDto(
        ChannelKind kind, ModuleKey moduleKey, ModuleQuantityGrant? grant, DateTimeOffset now)
    {
        // `25-115`'s own "do not fake data" warning: GrantedByOwner is true only while the owner's own
        // flag is actually live (set, and not past its own expiry) - never merely "was set at some
        // point," which is exactly the distinction ModuleQuantityGrant.EffectiveQuantity's own remarks
        // already draw between a lifted-or-lapsed flag and a live one.
        var ownerGrantIsLive = grant is { UnconditionallyGrantedByOwner: true }
            && (grant.UnconditionalGrantExpiresAt is not { } expiresAt || expiresAt > now);

        return new OwnerSiteChannelEntitlementDto(
            kind.ToString(),
            moduleKey.Value,
            Granted: (grant?.EffectiveQuantity(now) ?? 0) > 0,
            GrantedByOwner: ownerGrantIsLive,
            // The billing-driven case (Granted by Quantity > 0 alone) has no owner-set expiry to show -
            // this item's own warning against inventing data for the "paid" case not yet reachable.
            ExpiresAt: ownerGrantIsLive ? grant!.UnconditionalGrantExpiresAt : null);
    }
}
