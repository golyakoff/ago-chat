using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `23-68`: the record <c>RestoreOperatorSeatAsOwnerHandler</c> writes exactly once, only when the
/// override it names was actually exercised - the platform owner restoring a seat that pushes the
/// site's held-seat count past its own <see cref="Site.SeatLimit"/>. Never written when the restore
/// stayed within the limit: nothing was overridden there, so there is nothing to attest to - the
/// identical split <see cref="IModuleRevokeOverrideRepository"/>'s own remarks give for its sibling
/// table, reused here rather than re-argued because the shape of the decision is the same one, just
/// against a different limit ("seats" instead of "a tenant's own purchase").
///
/// <para><b>Its own port and its own table, not a column on <c>operators</c> or a widened
/// <see cref="IModuleRevokeOverrideRepository"/>.</b> A module-revoke override and a seat-restore
/// override name different subjects (a module key vs. an operator id) and are written by unrelated
/// handlers - forcing them through one port would mean a nullable-module-key/nullable-operator-id
/// shape on every row, the exact "one flag flips off every check" hazard this codebase's owner
/// surfaces already avoid elsewhere (<c>EnableModuleForSiteAsOwnerHandler</c>'s own remarks on why a
/// second command/handler beats a nullable branch). A second, narrow table costs one small file; a
/// widened one would cost every future reader having to learn which columns apply to which row
/// kind.</para>
///
/// <para><b>No domain aggregate, no business invariant beyond a (site, operator, actor, reason,
/// time) tuple</b> - the identical "no lifecycle an aggregate would protect" reasoning
/// <see cref="IModuleRevokeOverrideRepository"/>'s own remarks give for keeping that port, and this
/// one, out of the EF-tracked load-mutate-save world entirely.</para>
/// </summary>
public interface IOperatorSeatRestoreOverrideRepository
{
    /// <summary>Inserts one row. Always succeeds structurally - no foreign key to <c>sites</c> or
    /// <c>operators</c> to violate (see <see cref="OperatorSeatRestoreOverrideRecord"/>'s own remarks
    /// for why), and both rows named here were proven to exist moments earlier in the same handler
    /// call, when the <see cref="Operator"/> and <see cref="Site"/> rows it describes were
    /// loaded.</summary>
    Task RecordAsync(
        Guid id, SiteId siteId, OperatorId operatorId, string restoredBy, string reason, DateTimeOffset restoredAt,
        CancellationToken cancellationToken);

    /// <summary>Every override recorded for one tenant, oldest first - not consulted by anything this
    /// item builds (no console screen reads it back), kept narrow and site-scoped only so a real query
    /// exists to prove <see cref="RecordAsync"/> actually persisted, the same posture
    /// <see cref="IModuleRevokeOverrideRepository.ListForSiteAsync"/>'s own remarks state for
    /// itself.</summary>
    Task<IReadOnlyList<OperatorSeatRestoreOverrideRecord>> ListForSiteAsync(SiteId siteId, CancellationToken cancellationToken);
}

/// <summary>Read model for one recorded override - see
/// <see cref="IOperatorSeatRestoreOverrideRepository"/>'s own remarks on why no domain aggregate backs
/// it.
///
/// <para><b>No foreign key on <see cref="SiteId"/> or <see cref="OperatorId"/>, deliberately - the same
/// `adr/0111`/`adr/0112`/`adr/0113` mechanism <see cref="Abstractions.ModuleRevokeOverrideRecord"/>'s
/// own remarks restate.</b> A tenant whose seat-limit was overridden and who later closes their
/// account (or is erased) is exactly the tenant most likely to ask, later, "who let this operator back
/// in, and why" - a cascading foreign key would let the answer disappear with the account, which is
/// the one outcome this record exists to prevent.</para>
///
/// <para><see cref="Reason"/> is free text, on purpose, the identical
/// <see cref="ModuleRevokeOverrideRecord.Reason"/> shape - a justification that cannot name what
/// happened is not one.</para></summary>
public sealed record OperatorSeatRestoreOverrideRecord(
    Guid Id, SiteId SiteId, OperatorId OperatorId, string RestoredBy, string Reason, DateTimeOffset RestoredAt);
