namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `15-04`/`adr/0031`'s drop precondition: a <c>messages</c> partition past its retention horizon must
/// not be dropped until whatever it holds is confirmed recoverable some other way. `13-06` built the
/// real mechanism - <c>Ago.Chat.Infrastructure.Postgres.MessageArchiveGate</c>, backed by the
/// <c>message_archives</c> manifest <c>Ago.Chat.Worker</c>'s <c>MessageArchiveJob</c> writes only after
/// a real object-storage upload has already succeeded. `15-04`'s own stand-in
/// (<see cref="AlwaysConfirmedMessageArchiveGate"/>) remains as a test fake. Declared here, in
/// Application.Abstractions, rather than resolved ad hoc in <c>Ago.Chat.Worker</c> - CLAUDE.md rule 2: a
/// real implementation checks object storage (indirectly, through the manifest table archiving already
/// confirmed against), an external resource Application must not know the shape of, so the port belongs
/// on this side of the boundary.
///
/// <paramref name="partitionName"/> (on <see cref="IsArchivedAsync"/>) is the literal Postgres leaf
/// partition table name (e.g. <c>"messages_free_2026_01"</c>) rather than a decomposed (retention
/// class, month) pair - exactly as anticipated when this port was declared with one dimension and
/// widened to two without changing shape: <see cref="Ago.Chat.Infrastructure.Postgres.MessageArchiveGate"/>
/// reads what it needs straight out of the name (<c>MessagePartitionNames.TryParse</c>).
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
