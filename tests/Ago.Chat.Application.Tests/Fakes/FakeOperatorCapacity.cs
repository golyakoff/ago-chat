using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>
/// `6-09`: an in-memory <see cref="IOperatorCapacity"/> that records what a handler asked for. It
/// deliberately does not reproduce the real store's atomic compare-and-set - that is a claim about
/// Postgres, and testing.md puts it where it can actually be proven
/// (<c>OperatorCapacityStoreTests</c>, <c>CloseConversationCapacityConcurrencyTests</c>). What a
/// handler unit test can prove is the decision: whether a release was asked for at all, for whom, and
/// exactly once.
/// </summary>
public sealed class FakeOperatorCapacity : IOperatorCapacity
{
    private readonly List<OperatorId> _releases = [];

    public IReadOnlyList<OperatorId> Releases => _releases;

    public bool NextClaimSucceeds { get; set; } = true;

    /// <summary>`6-10`: makes <see cref="ReleaseAsync"/> behave the way the real store does when it has
    /// exhausted its bounded retry against a deadlocking <c>operators</c> row - the port's declared
    /// failure, never a raw <c>PostgresException</c>, which Application could not name anyway.</summary>
    public bool ReleaseAlwaysLosesToContention { get; set; }

    public Task<bool> TryClaimAsync(OperatorId operatorId, CancellationToken cancellationToken) =>
        Task.FromResult(NextClaimSucceeds);

    public Task ReleaseAsync(OperatorId operatorId, CancellationToken cancellationToken)
    {
        _releases.Add(operatorId);
        return ReleaseAlwaysLosesToContention
            ? Task.FromException(new OperatorCapacityContentionException(operatorId, attempts: 5, new InvalidOperationException("40P01")))
            : Task.CompletedTask;
    }
}
