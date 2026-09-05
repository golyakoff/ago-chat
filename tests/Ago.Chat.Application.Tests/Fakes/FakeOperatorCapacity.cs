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
    private readonly List<OperatorId> _claims = [];
    private readonly List<OperatorId> _unconditionalClaims = [];

    public IReadOnlyList<OperatorId> Releases => _releases;

    /// <summary>`18-02`: every operator <see cref="TryClaimAsync"/> was actually asked to claim for,
    /// regardless of outcome - <c>TransferConversationHandlerTests</c>' own way to prove which operator
    /// a transfer tried to charge, the same role <see cref="Releases"/> already plays for the other
    /// side.</summary>
    public IReadOnlyList<OperatorId> Claims => _claims;

    /// <summary>`23-04`: every operator <see cref="ClaimAsync"/> was actually asked to charge, the same
    /// role <see cref="Claims"/> plays for <see cref="TryClaimAsync"/> - kept as its own list rather
    /// than folded into <see cref="Claims"/> so a test can assert which of the two methods a handler
    /// called without inferring it from an outcome that, for this one, is always the same.</summary>
    public IReadOnlyList<OperatorId> UnconditionalClaims => _unconditionalClaims;

    /// <summary>`23-04`: makes <see cref="ClaimAsync"/> behave the way the real store does inside a
    /// caller-owned transaction that loses a Postgres deadlock - the port's declared failure, never a
    /// raw <c>PostgresException</c>. The identical role <see cref="ClaimAlwaysLosesToContention"/>
    /// plays for <see cref="TryClaimAsync"/>, kept separate so a test can pin which of the two calls
    /// fails.</summary>
    public bool UnconditionalClaimAlwaysLosesToContention { get; set; }

    public bool NextClaimSucceeds { get; set; } = true;

    /// <summary>`18-02`: the operators <see cref="TryClaimAsync"/> refuses regardless of
    /// <see cref="NextClaimSucceeds"/> - a transfer target genuinely at capacity, distinct from the
    /// global switch so a test can make one specific claim lose while every other capacity call in the
    /// same scenario still succeeds.</summary>
    public HashSet<OperatorId> ClaimFailsFor { get; } = [];

    /// <summary>`18-02`: makes <see cref="TryClaimAsync"/> behave the way the real store does inside a
    /// caller-owned transaction that loses a Postgres deadlock - the port's declared failure, never a
    /// raw <c>PostgresException</c>. Distinct from <see cref="ReleaseAlwaysLosesToContention"/> so a
    /// test can pin which of the transfer's two capacity calls is the one that fails this attempt.</summary>
    public bool ClaimAlwaysLosesToContention { get; set; }

    /// <summary>`6-10`: makes <see cref="ReleaseAsync"/> behave the way the real store does when it has
    /// exhausted its bounded retry against a deadlocking <c>operators</c> row - the port's declared
    /// failure, never a raw <c>PostgresException</c>, which Application could not name anyway.</summary>
    public bool ReleaseAlwaysLosesToContention { get; set; }

    public Task<bool> TryClaimAsync(OperatorId operatorId, CancellationToken cancellationToken)
    {
        _claims.Add(operatorId);
        if (ClaimAlwaysLosesToContention)
        {
            return Task.FromException<bool>(
                new OperatorCapacityContentionException(operatorId, attempts: 1, new InvalidOperationException("40P01")));
        }

        return Task.FromResult(NextClaimSucceeds && !ClaimFailsFor.Contains(operatorId));
    }

    public Task ReleaseAsync(OperatorId operatorId, CancellationToken cancellationToken)
    {
        _releases.Add(operatorId);
        return ReleaseAlwaysLosesToContention
            ? Task.FromException(new OperatorCapacityContentionException(operatorId, attempts: 5, new InvalidOperationException("40P01")))
            : Task.CompletedTask;
    }

    public Task ClaimAsync(OperatorId operatorId, CancellationToken cancellationToken)
    {
        _unconditionalClaims.Add(operatorId);
        return UnconditionalClaimAlwaysLosesToContention
            ? Task.FromException(new OperatorCapacityContentionException(operatorId, attempts: 1, new InvalidOperationException("40P01")))
            : Task.CompletedTask;
    }
}
