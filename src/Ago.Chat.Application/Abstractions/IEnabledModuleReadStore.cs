using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `20-07`: the hot read path - "a site's enabled modules and their trigger arrays" - adr/0004's Dapper
/// side. Two real callers: <c>EnableModuleForSiteHandler</c>'s own trigger-overlap check (registration
/// time, low frequency) and the message pipeline's trigger match (every visitor message, on every site
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

    /// <summary>`23-14`: every module this site has ever had enabled, expired ones included - the
    /// diagnostic sibling of <see cref="GetForSiteAsync"/> above, for the platform owner's per-tenant
    /// detail read alone. <see cref="GetForSiteAsync"/>'s `WHERE` clause is what production trusts to
    /// decide "may this module act for this site right now", and that stays exactly as it is: a tenant
    /// configuring their own site (`23-01`'s `ListEnabledModulesForSite`) has no reason to see a lapsed
    /// grant chat has already stopped honouring. A support agent repairing a tenant does
    /// (`flows.md` 5.3) - "the module vanished" and "the module was never granted" are different facts,
    /// and only this method can tell them apart.
    ///
    /// <para><see cref="EnabledModuleDetailSummary.IsActive"/> is computed by the identical
    /// `expires_at is null or expires_at > @Now` comparison <see cref="GetForSiteAsync"/>'s own `WHERE`
    /// clause already uses - projected here instead of filtered, so a caller reading it is trusting the
    /// same decision the hot path makes, not a second one computed against a different clock
    /// (`CLAUDE.md` rule 11: instants come from `IClock`, and this is what keeps that single source of
    /// truth even when the SQL shape differs).</para></summary>
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
/// wire), plus <see cref="IsActive"/> - the one fact <see cref="EnabledModuleSummary"/> does not need
/// to carry, because <see cref="IEnabledModuleReadStore.GetForSiteAsync"/> already expresses "active"
/// by including the row at all.</summary>
public sealed record EnabledModuleDetailSummary(
    ModuleKey ModuleKey, IReadOnlyList<string> TriggerWords, Uri EntryPoint, bool GrantedByOwner,
    DateTimeOffset? ExpiresAt, bool IsActive);
