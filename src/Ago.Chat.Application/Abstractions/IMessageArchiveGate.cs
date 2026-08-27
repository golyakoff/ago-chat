namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `15-04`/`adr/0031`'s drop precondition: a <c>messages</c> partition past its retention horizon must
/// not be dropped until whatever it holds is confirmed recoverable some other way. `13-06` owns the
/// real mechanism - one archive object per site per period, written to object storage before the drop -
/// and is not built yet; this item only owns the pruning job that must call a check before every drop,
/// per its own scope note ("the drop step must be structured so 13-06's not-yet-built archive-
/// confirmation can gate it later"). Declared here, in Application.Abstractions, rather than resolved
/// ad hoc in <c>Ago.Chat.Worker</c> - CLAUDE.md rule 2: a real implementation checks object storage, an
/// external resource Application must not know the shape of, so the port belongs on this side of the
/// boundary even though nothing behind it does I/O yet.
///
/// <paramref name="partitionName"/> (on <see cref="IsArchivedAsync"/>) is the literal Postgres
/// partition table name (e.g. <c>"messages_2026_01"</c>) rather than a decomposed (retention class,
/// month) pair. Today's grid has one dimension; `13-06` widens it to two by renaming partitions to
/// carry the class as well (`adr/0031`'s "PARTITION BY LIST (retention_class), each itself PARTITION BY
/// RANGE (created_at)"). Passing the name whole means this port's *shape* does not need to change when
/// that dimension lands - a real implementation reads whatever it needs out of the name (or, more
/// likely, out of its own archive-manifest table keyed the same way), and this port stays exactly the
/// "has this partition's data been archived" question it is today.
/// </summary>
public interface IMessageArchiveGate
{
    /// <summary>True once every row <paramref name="partitionName"/> holds is safely recoverable
    /// without that partition - i.e. the partition is safe to <c>DROP</c>. <paramref name="periodStart"/>
    /// (inclusive) and <paramref name="periodEnd"/> (exclusive) are the partition's own
    /// <c>created_at</c> range, handed over pre-parsed so a real implementation never has to
    /// re-derive them from the name.</summary>
    Task<bool> IsArchivedAsync(
        string partitionName, DateOnly periodStart, DateOnly periodEnd, CancellationToken cancellationToken);
}
