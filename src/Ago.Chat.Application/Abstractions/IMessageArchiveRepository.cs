using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `13-06`/`adr/0031`: the manifest of every archive object actually written - one row per site per
/// retention class per period, recorded only once <c>Ago.Chat.Worker</c>'s <c>MessageArchiveJob</c> has
/// confirmed the upload to object storage succeeded. Two readers depend on this table for two different
/// reasons: <see cref="IMessageArchiveGate"/>'s real implementation reads it to decide whether a
/// partition is safe to <c>DROP</c> (the ordering `adr/0031` is built around - nothing is dropped until
/// this table says its data is recoverable some other way), and this port's own listing/lookup methods
/// serve the tenant-facing retrieval read (a site can only ask for what this table already knows about).
///
/// <para>No domain aggregate behind it, the same reasoning <see cref="IExportRequestRepository"/>'s own
/// remarks give for <c>export_requests</c>: a manifest row with no business invariant beyond "one
/// (site, class, period) triple, written once" has nothing for an EF change-tracked load-mutate-save to
/// protect. <see cref="RecordAsync"/> is insert-only and idempotent by construction (unique on
/// `(site_id, retention_class, period_start)`) - a job retrying after a partial failure never produces a
/// second row for a period it already finished archiving.</para>
/// </summary>
public interface IMessageArchiveRepository
{
    /// <summary>Records that <paramref name="siteId"/>'s messages for
    /// <paramref name="retentionClass"/>/<paramref name="periodStart"/> are now archived at
    /// <paramref name="objectKey"/>. Called only after the upload to object storage has already
    /// succeeded - see <c>MessageArchiveJob</c>'s own remarks for why recording here is the very last
    /// step, never the first. A no-op (not an error) if this exact triple is already recorded - the
    /// same "a retry after a crash mid-cycle must not double-write" idempotency every other job in this
    /// codebase's retention/pruning family already relies on.</summary>
    Task RecordAsync(
        Guid id, SiteId siteId, RetentionClass retentionClass, DateOnly periodStart, DateOnly periodEnd,
        string objectKey, DateTimeOffset archivedAt, CancellationToken cancellationToken);

    /// <summary>Every distinct site id already archived for <paramref name="retentionClass"/>/
    /// <paramref name="periodStart"/> - what <see cref="IMessageArchiveGate"/>'s real implementation
    /// compares against a partition's own distinct site ids to decide whether every one of them has
    /// been accounted for.</summary>
    Task<IReadOnlySet<Guid>> ListArchivedSiteIdsAsync(
        RetentionClass retentionClass, DateOnly periodStart, CancellationToken cancellationToken);

    /// <summary>Every archived period this site can currently request a download for, newest first -
    /// what the tenant-facing "which periods are available" read serves.</summary>
    Task<IReadOnlyList<MessageArchiveRecord>> ListForSiteAsync(SiteId siteId, CancellationToken cancellationToken);

    /// <summary>One archive, scoped by site as well as its own key - the same cross-tenant guard
    /// <see cref="IExportRequestRepository.GetAsync"/> already establishes: a site asking for another
    /// tenant's archive id gets <see langword="null"/>, indistinguishable from one that never
    /// existed.</summary>
    Task<MessageArchiveRecord?> GetAsync(
        SiteId siteId, RetentionClass retentionClass, DateOnly periodStart, CancellationToken cancellationToken);
}

public sealed record MessageArchiveRecord(
    Guid Id, SiteId SiteId, RetentionClass RetentionClass, DateOnly PeriodStart, DateOnly PeriodEnd,
    string ObjectKey, DateTimeOffset ArchivedAt);
