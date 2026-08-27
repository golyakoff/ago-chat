namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// The stand-in for <see cref="IMessageArchiveGate"/> until `13-06` exists - see that interface's own
/// remarks for the full reasoning. Confirms every partition unconditionally, which is honest today:
/// nothing archives yet, so "confirmed archived" and "there is nothing this system promises to keep
/// that dropping would lose" are the same fact until `13-06` changes what dropping actually means.
/// Registered as the default implementation in <c>ChatModule</c> so <c>MessagePartitionPruneJob</c> is
/// fully gated by construction - it always calls the port, never checks whether one was configured -
/// rather than the job needing a null-check fallback of its own. No I/O, so it needs no
/// <c>Infrastructure.*</c> project: CLAUDE.md rule 2 is about a port to an *external resource*, and this
/// class touches nothing external, it is a fixed answer that happens to satisfy the same interface a
/// real, I/O-performing implementation will later satisfy.
/// </summary>
public sealed class AlwaysConfirmedMessageArchiveGate : IMessageArchiveGate
{
    public Task<bool> IsArchivedAsync(
        string partitionName, DateOnly periodStart, DateOnly periodEnd, CancellationToken cancellationToken) =>
        Task.FromResult(true);
}
