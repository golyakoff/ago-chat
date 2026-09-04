namespace Ago.Chat.Contracts;

/// <summary>
/// `23-14`: `GET /api/v1/owner/sites/{siteId}`'s response body - the same eight facts
/// <see cref="OwnerSiteSummaryDto"/> carries for a page of sites, computed for exactly one tenant the
/// owner named, plus that tenant's entitlements (<see cref="Modules"/>). Two use cases, one screen -
/// the owner searches the list, then drills into a row - which is why the shared fields below are
/// deliberately the same names and meanings as <see cref="OwnerSiteSummaryDto"/> rather than a
/// differently-worded second vocabulary for the same numbers.
/// </summary>
/// <param name="RecentWindowDays">Same field, same meaning as <see cref="OwnerSitesResponse.RecentWindowDays"/> -
/// repeated here rather than assumed equal, since a console screen reached by a direct link (not by
/// drilling in from the list) never sees the list's own response at all.</param>
/// <param name="Modules">Every module this site has ever had enabled, expired grants included -
/// deliberately not `23-01`'s `ListEnabledModulesForSite`'s "currently active only" shape. A support
/// agent repairing a tenant (`flows.md` 5.3) needs to see a lapsed trial, not just its absence; the
/// tenant's own `/settings` screen has no such need and keeps calling the filtered read
/// unchanged.</param>
public sealed record OwnerSiteDetailResponse(
    Guid SiteId,
    string Name,
    string Tier,
    DateTimeOffset? CreatedAt,
    long SeatCount,
    long ConversationCount,
    long RecentMessageCount,
    DateTimeOffset? LastMessageAt,
    long AttachmentBytes,
    int RecentWindowDays,
    IReadOnlyList<OwnerSiteModuleDto> Modules);

/// <summary>
/// `23-14`: one row of <see cref="OwnerSiteDetailResponse.Modules"/> - a module this site has (or had)
/// enabled, as the platform owner needs to see it to tell a tenant's own purchase apart from an
/// owner's grant and to know whether either is still in force. Never carries the module's
/// <c>Credential</c> - the same "a secret is accepted, never returned" hygiene
/// `ModuleEndpoints.EnableModuleResponse`'s own remarks describe for the tenant-facing shape this
/// mirrors.
/// </summary>
/// <param name="GrantedByOwner"><see langword="true"/> when the platform owner enabled this module
/// rather than the tenant's own operator - the wire-visible half of `22-17`'s audit distinction, the
/// identical field `ModuleEndpoints.EnableModuleResponse` already carries for the tenant-scoped
/// listing.</param>
/// <param name="ExpiresAt"><see langword="null"/> for a grant that does not expire - rendered by the
/// console as an explicit "no end date", never as a blank cell (this item's own Done-when). A
/// self-service, tenant-purchased module is always <see langword="null"/> here
/// (`Domain.EnabledModule.ExpiresAt`'s own remarks: "a tenant who paid did not buy a trial").</param>
/// <param name="IsActive"><see langword="false"/> once <see cref="ExpiresAt"/> has passed - computed
/// once, server-side, by the identical `expires_at is null or expires_at > now` comparison the
/// production read path already uses to decide whether chat still offers this module
/// (`IEnabledModuleReadStore.GetForSiteAsync`'s own remarks), so the console renders this flag
/// directly rather than comparing <see cref="ExpiresAt"/> against its own clock (this item's own
/// Done-when: "matching what the live read-store query already decides rather than re-deriving it in
/// the console").</param>
public sealed record OwnerSiteModuleDto(
    string ModuleKey,
    IReadOnlyList<string> TriggerWords,
    string EntryPoint,
    bool GrantedByOwner,
    DateTimeOffset? ExpiresAt,
    bool IsActive);
