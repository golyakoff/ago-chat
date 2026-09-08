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
/// <param name="AllowedOrigins">`23-48`: the tenant's own current <c>Site.AllowedOrigins</c>, added so
/// the owner's detail screen - the only place any of it may now be edited - has something to show and
/// edit without a second round trip. Every entry is already in normalized form
/// (<c>Ago.Chat.Application.UseCases.RegisterSite.OriginValidator</c> refuses anything else at write
/// time), so this list needs no further formatting to be shown back verbatim.</param>
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
    IReadOnlyList<OwnerSiteModuleDto> Modules,
    IReadOnlyList<string> AllowedOrigins);

/// <summary>
/// `23-14`: one row of <see cref="OwnerSiteDetailResponse.Modules"/> - a module this site has (or had)
/// enabled, as the platform owner needs to see it to tell a tenant's own purchase apart from an
/// owner's grant and to know whether either is still in force. Never carries the module's
/// <c>Credential</c> - the same "a secret is accepted, never returned" hygiene
/// `ModuleEndpoints.EnableModuleResponse`'s own remarks describe for the tenant-facing shape this
/// mirrors.
/// </summary>
/// <param name="Id">`23-103`: this row's own identity - <c>enabled_modules.id</c>, a raw
/// <see cref="Guid"/> here (never <c>Ago.Chat.Domain.EnabledModuleId</c> - this project has no reference
/// to <c>Ago.Chat.Domain</c> and must not gain one for this, the same "the wire carries values, not
/// vocabulary" shape <see cref="OperatorPermissionsResponse.Permissions"/>'s own remarks already state
/// for a differently-shaped case). Needed because <see cref="ModuleKey"/> is no longer a reliable
/// per-row key: `adr/0155` records that a revoke-then-re-grant leaves two rows for one
/// (site, module) pair, both returned by this list, and a caller rendering more than one row for the
/// same module needs something to tell them apart - a stable list key, and "which row is this action
/// about" once actions are scoped per row rather than per module key.</param>
/// <param name="GrantedByOwner"><see langword="true"/> when the platform owner enabled this module
/// rather than the tenant's own operator - the wire-visible half of `22-17`'s audit distinction, the
/// identical field `ModuleEndpoints.EnableModuleResponse` already carries for the tenant-scoped
/// listing.</param>
/// <param name="ExpiresAt"><see langword="null"/> for a grant that does not expire - rendered by the
/// console as an explicit "no end date", never as a blank cell (this item's own Done-when). A
/// self-service, tenant-purchased module is always <see langword="null"/> here
/// (`Domain.EnabledModule.ExpiresAt`'s own remarks: "a tenant who paid did not buy a trial"). Still
/// present after `23-103` even when <see cref="Status"/> is <c>"Revoked"</c> - a grant can be both
/// expired and revoked, and losing the expiry once a revoke is the reported reason would hide a fact
/// the row still genuinely carries.</param>
/// <param name="RevokedAt">`23-103`: <see langword="null"/> for a grant that has never been revoked,
/// otherwise the moment the platform owner revoked it - the same "the row says when" shape
/// <see cref="ExpiresAt"/> already gives for its own end date, so a revoked row is never rendered with
/// a status and no timestamp behind it.</param>
/// <param name="Status">`23-103`: one of <c>"Active"</c>, <c>"Expired"</c> or <c>"Revoked"</c> - what
/// this module grant actually is right now, not merely whether it may still act (which
/// <c>Status == "Active"</c> alone already answers, replacing the old <c>IsActive</c> boolean this
/// field carried before). Computed once, server-side, from the identical row this whole DTO is
/// projected from (`IEnabledModuleReadStore.GetAllForSiteAsync`'s own remarks) - the console renders
/// this value directly, never re-deriving "why inactive" from <see cref="ExpiresAt"/>/
/// <see cref="RevokedAt"/> against its own clock or a second read (this item's own warning: no second
/// source of truth for a decision the server already made). A grant that is both expired and revoked
/// reads <c>"Revoked"</c> - see `Ago.Chat.Application.Abstractions.EnabledModuleDetailSummary`'s own
/// remarks for why that precedence, not the reverse.</param>
/// <param name="Quantity">`23-66`: this module's own granted countable quantity - the calendar
/// add-on's "N masters" is the first real instance, opaque here exactly like <see cref="ModuleKey"/>
/// itself. <see langword="null"/> when no quantity was ever granted for this module, distinct from
/// <c>0</c> (a quantity explicitly granted as zero) - collapsing the two would tell a platform owner
/// "not granted" when a tenant with the module and zero workers is a legitimate state
/// (`IModuleQuantityGrantStore.GetAllForSiteAsync`'s own remarks state the same distinction on the
/// read side this field is sourced from). `23-103`: unchanged by this item even though a module can now
/// carry two rows after a revoke-then-re-grant - the quantity grant is its own snapshot aggregate, one
/// row per (site, module) by construction (`Domain.ModuleQuantityGrant`'s own remarks), so every row
/// sharing a <see cref="ModuleKey"/> in this list reports the identical current value. That is a
/// pre-existing property of the quantity grant, not a new decision this item makes.</param>
public sealed record OwnerSiteModuleDto(
    Guid Id,
    string ModuleKey,
    IReadOnlyList<string> TriggerWords,
    string EntryPoint,
    bool GrantedByOwner,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    string Status,
    int? Quantity);
