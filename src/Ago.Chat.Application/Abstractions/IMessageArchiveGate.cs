using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `15-04`/`adr/0031`'s removal precondition, restated for `15-09`/`adr/0087`'s `DELETE`-based
/// mechanism: a tenant's messages for one (retention class, period) must not be removed until they are
/// confirmed recoverable some other way. `13-06` built the real mechanism -
/// <c>Ago.Chat.Infrastructure.Postgres.MessageArchiveGate</c>, backed by the <c>message_archives</c>
/// manifest <c>Ago.Chat.Worker</c>'s <c>MessageArchiveJob</c> writes only after a real object-storage
/// upload has already succeeded. `15-04`'s own stand-in (<see cref="AlwaysConfirmedMessageArchiveGate"/>)
/// remains as a test fake. Declared here, in Application.Abstractions, rather than resolved ad hoc in
/// <c>Ago.Chat.Worker</c> - CLAUDE.md rule 2: a real implementation checks object storage (indirectly,
/// through the manifest table archiving already confirmed against), an external resource Application
/// must not know the shape of, so the port belongs on this side of the boundary.
///
/// <para><b>Simplified by `15-09`, not just re-signatured.</b> Before this item, one partition held every
/// tenant's rows for a (class, period), so confirming a partition safe to drop meant checking *every*
/// distinct `site_id` it held against the manifest - <paramref name="siteId"/> did not exist as a
/// parameter because the caller did not yet know which sites were involved. Under `HASH (site_id)`
/// partitioning, the removal unit is already scoped to one site before this gate is ever asked (the
/// prune sweep discovers `(site_id, retention_class, period)` tuples directly, one per query), so this
/// answers a single-row existence question against `message_archives` instead of a whole-partition
/// aggregate one.</para>
/// </summary>
public interface IMessageArchiveGate
{
    /// <summary>True once <paramref name="siteId"/>'s messages for <paramref name="retentionClass"/>/
    /// <paramref name="periodStart"/> are safely recoverable without them - i.e. that slice is safe to
    /// remove.</summary>
    Task<bool> IsArchivedAsync(
        SiteId siteId, RetentionClass retentionClass, DateOnly periodStart, CancellationToken cancellationToken);
}
