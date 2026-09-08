using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `20-07`: the hot read path - "a site's enabled modules and their trigger arrays" - adr/0004's Dapper
/// side. Two real callers: the module-registration trigger-overlap check (registration time, low
/// frequency - `23-83`/`adr/0151`: only <c>EnableModuleForSiteAsOwnerHandler</c> registers a module at
/// all now, the tenant's own self-service <c>EnableModuleForSiteHandler</c> having been removed rather
/// than kept) and the message pipeline's trigger match (every visitor message, on every site
/// with at least one module enabled - the reason this is a read store and not a plain EF query
/// through <see cref="IEnabledModuleRepository"/>, matching `caching.md`'s reasoning for every other
/// per-message read in this codebase).
/// </summary>
public interface IEnabledModuleReadStore
{
    /// <param name="now">`22-17`: an expired grant (<see cref="EnabledModule.ExpiresAt"/> at or before
    /// this instant) is excluded from the result - rule 8's "a write decision never reads a cache"
    /// applied to this read: every caller of this method (the trigger-conflict check, the message
    /// pipeline's own trigger match, and the console's own <c>GET .../modules</c> listing) is deciding
    /// whether a module may act for this site *right now*, so the expiry has to be evaluated against
    /// the same "now" the caller is deciding for, sourced from <c>IClock</c> like every other instant
    /// this codebase compares (`CLAUDE.md` rule 11) - never the database's own clock.</param>
    Task<IReadOnlyList<EnabledModuleSummary>> GetForSiteAsync(
        SiteId siteId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>`23-14`: every module this site has ever had enabled, expired and revoked ones
    /// included - the diagnostic sibling of <see cref="GetForSiteAsync"/> above, for the platform
    /// owner's per-tenant detail read alone. <see cref="GetForSiteAsync"/>'s `WHERE` clause is what
    /// production trusts to decide "may this module act for this site right now", and that stays
    /// exactly as it is: a tenant configuring their own site (`23-01`'s `ListEnabledModulesForSite`)
    /// has no reason to see a lapsed grant chat has already stopped honouring. A support agent
    /// repairing a tenant does (`flows.md` 5.3) - "the module vanished" and "the module was never
    /// granted" are different facts, and only this method can tell them apart.
    ///
    /// <para><see cref="EnabledModuleDetailSummary.Status"/> is computed by the identical
    /// `expires_at is null or expires_at > @Now` / `revoked_at is null` comparisons
    /// <see cref="GetForSiteAsync"/>'s own `WHERE` clause already uses - projected here instead of
    /// filtered, so a caller reading it is trusting the same decision the hot path makes, not a second
    /// one computed against a different clock (`CLAUDE.md` rule 11: instants come from `IClock`, and
    /// this is what keeps that single source of truth even when the SQL shape differs). `23-103`:
    /// before this item the projection was a single <c>IsActive</c> boolean, which could not
    /// distinguish "expired" from "revoked" once `22-30` stopped deleting a revoked row - see
    /// <see cref="EnabledModuleDetailSummary"/>'s own remarks for the three-value shape that
    /// replaced it, and its precedence when a grant is both.</para>
    ///
    /// <para>`23-103`: rows are returned ordered by <c>enabled_at</c> ascending - the order a site
    /// actually acquired each grant in, oldest first - so a caller rendering more than one row for the
    /// same <see cref="ModuleKey"/> (`adr/0155`'s own "a revoked-then-re-enabled module leaves two
    /// rows on chat's own side") gets a stable, meaningful order for free rather than whatever order
    /// Postgres happens to return with no `ORDER BY` at all.</para></summary>
    Task<IReadOnlyList<EnabledModuleDetailSummary>> GetAllForSiteAsync(
        SiteId siteId, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>Exactly what <see cref="TriggerCommandMatcher"/> and the registration-time overlap check
/// need, and nothing else about an <see cref="EnabledModule"/> row - no <see cref="EnabledModuleId"/>,
/// which neither caller has any use for.
///
/// <para><b>`22-02`: <see cref="Credential"/> rides along even though neither of those two callers
/// reads it.</b> The one caller that does - <c>RouteConversationToModuleHandler</c>, building an
/// <see cref="EnabledModuleEndpoint"/> per call - already reads this same row for
/// <see cref="EntryPoint"/>, so carrying the credential here avoids a second read store method for a
/// single extra field.</para>
///
/// <para><b>`22-17`: <see cref="GrantedByOwner"/> rides along for the identical reason.</b> Neither
/// the trigger-conflict check nor the message pipeline reads it, but <c>ModuleEndpoints</c>'s own
/// console listing does - it is the wire-visible half of this item's own audit-distinction
/// requirement, the same "avoid a second read-store method for one more field" judgement
/// <see cref="Credential"/>'s own remarks already made.</para></summary>
public sealed record EnabledModuleSummary(
    ModuleKey ModuleKey, IReadOnlyList<string> TriggerWords, Uri EntryPoint, ModuleCredential Credential,
    bool GrantedByOwner, DateTimeOffset? ExpiresAt);

/// <summary>`23-14`: one row of <see cref="IEnabledModuleReadStore.GetAllForSiteAsync"/> - the same
/// facts <see cref="EnabledModuleSummary"/> carries for the production hot path, minus
/// <see cref="EnabledModuleSummary.Credential"/> (the platform owner's detail read never needs it, the
/// same "never echoed back" hygiene <c>ModuleEndpoints.EnableModuleResponse</c> already applies to the
/// wire), plus the facts <see cref="EnabledModuleSummary"/> does not need to carry because
/// <see cref="IEnabledModuleReadStore.GetForSiteAsync"/> already expresses "not usable right now" by
/// excluding the row entirely: <see cref="Id"/>, <see cref="RevokedAt"/> and <see cref="Status"/>.
///
/// <para><b>`23-103`: <see cref="Id"/> is new.</b> Before this item nothing here carried
/// <see cref="EnabledModuleId"/> - "no caller has any use for it", <see cref="EnabledModuleSummary"/>'s
/// own remarks say, correctly, for its two callers. This method's own caller now does: `adr/0155`
/// records that a revoke-then-re-grant leaves two rows in `enabled_modules` for the same
/// <see cref="SiteId"/>/<see cref="ModuleKey"/> pair, and <see cref="ModuleKey"/> alone cannot tell
/// those two rows apart for a caller that must (a stable list-rendering key, "which row is this
/// action about"). The row's own primary key is what already answers that, at no extra query - carried
/// as the strongly-typed <see cref="EnabledModuleId"/> here, the same as every other Domain-facing
/// identifier this Application-layer record already uses (<see cref="ModuleKey"/>,
/// <see cref="EntryPoint"/>); <c>Ago.Chat.Contracts.OwnerSiteModuleDto</c> is where it flattens to a
/// raw <see cref="Guid"/> for the wire.</para>
///
/// <para><b>`23-103`: <see cref="Status"/> replaces the old <c>IsActive</c> boolean.</b> Before `22-30`
/// tombstoned a revoke instead of deleting the row, "not active" could only mean "expired" - the
/// two-way mapping a boolean expressed was true. It stopped being true the moment a revoked row could
/// also appear here, and nothing on the read side followed until this item
/// (`docs/backlog/23-103-*.md`). One of three raw string values - <c>"Active"</c>, <c>"Expired"</c> or
/// <c>"Revoked"</c>, never a `Ago.Chat.Domain` enum type on this wire-facing shape (the same "the wire
/// carries values, not vocabulary" discipline `OperatorPermissionsResponse`'s own remarks state for
/// its own raw permission strings) - computed once, in SQL, from the identical row and the identical
/// `@Now` this method's own `IsActive` predecessor used, never re-derived by a caller against its own
/// clock.</para>
///
/// <para><b>Precedence when a grant is both expired and revoked:</b> <see cref="Status"/> reads
/// <c>"Revoked"</c>. A revoke is a deliberate, timestamped act by the platform owner; an expiry is a
/// grant's own end date quietly arriving. When both are true the revoke is the more specific and more
/// recent fact - it is *why* the row stopped mattering, even for a grant that would also have lapsed
/// on its own - so it is the one fact worth surfacing. <see cref="RevokedAt"/> still carries the
/// timestamp regardless (mirroring <see cref="ExpiresAt"/>'s own "the row says when"), so nothing about
/// the expiry is lost even when it is not what <see cref="Status"/> names.</para></summary>
public sealed record EnabledModuleDetailSummary(
    EnabledModuleId Id, ModuleKey ModuleKey, IReadOnlyList<string> TriggerWords, Uri EntryPoint,
    bool GrantedByOwner, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt, string Status);
