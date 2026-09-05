using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `23-13`: the record `RevokeModuleForSiteAsOwnerHandler` writes exactly once, only when the
/// override it names was actually exercised - the platform owner revoking a tenant's own self-service
/// purchase (<see cref="EnabledModule.GrantedByOwner"/> <see langword="false"/>) with the request's
/// force flag set. Never written for an owner revoking their own grant, force flag or not: nothing was
/// overridden there, so there is nothing to attest to (the same "a read that fails authorisation is not
/// recorded, because nothing was read" shape `adr/0113` gives its own <c>access_records</c>).
///
/// <para><b>Its own port, not a method on <see cref="IEnabledModuleRepository"/>.</b> The row this
/// writes has no lifecycle an aggregate would protect - it is minted once and never read back by
/// anything this item builds - the identical "no domain aggregate, no business invariant beyond a
/// (site, actor, reason, time) tuple" reasoning <see cref="IExportRequestRepository"/>'s own remarks
/// give for keeping that port, and this one, out of the EF-tracked load-mutate-save world entirely.
/// </para>
///
/// <para><b>Its own table, not a column on <c>enabled_modules</c>.</b> `adr/0098`'s own "a second
/// table would be a second place the facts could drift" argument governs <see cref="EnabledModule.GrantedByOwner"/>/
/// <see cref="EnabledModule.ExpiresAt"/> precisely because those columns describe a row that keeps
/// existing. This record describes an act against a row that is, by the time this port is called, one
/// statement away from being deleted (<see cref="IEnabledModuleRepository.DeleteAsync"/>) - there is no
/// still-existing row left for a column to drift from, which is the one case that argument does not
/// reach.</para>
/// </summary>
public interface IModuleRevokeOverrideRepository
{
    /// <summary>Inserts one row. Always succeeds structurally - unlike
    /// <see cref="IExportRequestRepository.CreateAsync"/>'s <c>where exists</c> guard, this carries no
    /// foreign key to <c>sites</c> to violate (see <see cref="ModuleRevokeOverrideRecord"/>'s own
    /// remarks for why), and the site named here was proven to exist moments earlier in the same
    /// handler call, when the <see cref="EnabledModule"/> row it describes was loaded.</summary>
    Task RecordAsync(
        Guid id, SiteId siteId, string moduleKey, string revokedBy, string reason, DateTimeOffset revokedAt,
        CancellationToken cancellationToken);

    /// <summary>Every override recorded for one tenant, oldest first - not consulted by anything this
    /// item builds (no console screen, `23-13`'s own Out of scope), kept narrow and site-scoped only so
    /// a real query exists to prove <see cref="RecordAsync"/> actually persisted, the same "a write port
    /// with nothing yet reading it still gets the one read a real Postgres round-trip test needs"
    /// posture <see cref="IExportRequestRepository.GetAsync"/> takes for its own sibling.</summary>
    Task<IReadOnlyList<ModuleRevokeOverrideRecord>> ListForSiteAsync(SiteId siteId, CancellationToken cancellationToken);
}

/// <summary>Read model for one recorded override - see <see cref="IModuleRevokeOverrideRepository"/>'s
/// own remarks on why no domain aggregate backs it.
///
/// <para><b>No foreign key on <see cref="SiteId"/>, deliberately - the fourth instance of
/// `adr/0111`/`adr/0112`/`adr/0113`'s own reasoning, not a fresh judgement call.</b> There, evidence had
/// to survive the erasure or deletion of the very thing it is evidence of. Here: a tenant whose purchase
/// was overridden and who later closes their account (or is erased) is exactly the tenant most likely to
/// ask, later, "who took this away from me, and why" - a cascading foreign key would let the answer
/// disappear with the account, which is the one outcome this record exists to prevent.</para>
///
/// <para><see cref="Reason"/> is free text, on purpose, unlike <see cref="Domain.EnabledModule"/> and
/// unlike `adr/0112`'s own <c>failure_reason</c> (an exception type name, never a message, because a
/// message can quote what is being erased). This item's whole point is that the override must be
/// *justified*, and a justification that cannot name what happened is not one - `decisions.md` §6's own
/// "who, when, which tenant, and why" is explicit that the why is free text, not an enum.</para></summary>
public sealed record ModuleRevokeOverrideRecord(
    Guid Id, SiteId SiteId, string ModuleKey, string RevokedBy, string Reason, DateTimeOffset RevokedAt);
