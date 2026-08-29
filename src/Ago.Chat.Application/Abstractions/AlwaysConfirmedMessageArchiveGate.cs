namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `15-04`'s stand-in for <see cref="IMessageArchiveGate"/>, kept after `13-06` shipped the real,
/// object-storage-backed implementation (`Ago.Chat.Infrastructure.Postgres.MessageArchiveGate`, now
/// the one registered by <c>AddPostgresPersistence</c>) as a lightweight always-true fake for tests that
/// need a working gate but are not themselves testing archive-confirmation logic - the same role
/// <c>FakeRateLimiter</c>/<c>NoOpCache</c> play for their own ports elsewhere in this codebase's test
/// suites. No I/O, so it needs no <c>Infrastructure.*</c> project: CLAUDE.md rule 2 is about a port to
/// an *external resource*, and this class touches nothing external.
/// </summary>
public sealed class AlwaysConfirmedMessageArchiveGate : IMessageArchiveGate
{
    public Task<bool> IsArchivedAsync(
        string partitionName, DateOnly periodStart, DateOnly periodEnd, CancellationToken cancellationToken) =>
        Task.FromResult(true);
}
