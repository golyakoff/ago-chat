using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `6-10`: <see cref="IOperatorCapacity.ReleaseAsync"/>'s own technology-agnostic signal that the
/// store could not apply the decrement because it kept losing to concurrent writers on the same
/// <c>operators</c> row - in practice a Postgres deadlock (`SqlState 40P01`) the adapter already
/// retried its bounded number of times.
///
/// Declared here, next to the port, for exactly the reason
/// <see cref="ConversationConcurrencyConflictException"/> is (`6-08`): clean-architecture.md's
/// dependency rule keeps `Ago.Chat.Application` free of any Npgsql reference, so the adapter
/// (`Ago.Chat.Infrastructure.Postgres.OperatorCapacityStore`) is the one place that knows the
/// underlying failure is a <c>PostgresException</c>, and it translates at the port boundary before
/// anything reaches a handler. A handler must never catch <c>PostgresException</c>, and an operator
/// must never see one.
///
/// <para>What a caller does with it is a use-case decision, not a storage one, and the two callers
/// answer differently on purpose: <c>CloseConversationHandler</c> has already committed the close, so
/// it keeps the request successful and accepts a one-slot leak (the same bounded residual `6-09`
/// documents for a process death in that window); <c>OperatorConversationReleaser</c> runs inside a
/// broker consumer whose whole delivery is redelivered, so it lets this propagate.</para>
/// </summary>
public sealed class OperatorCapacityContentionException(OperatorId operatorId, int attempts, Exception innerException)
    : Exception(
        $"Operator {operatorId.Value}'s capacity row could not be updated after {attempts} attempt(s) because of write contention.",
        innerException)
{
    public OperatorId OperatorId { get; } = operatorId;

    /// <summary>How many attempts the adapter actually made before giving up - 1 when the call ran
    /// inside a caller-owned transaction, where retrying the statement is impossible (the deadlock
    /// aborted the whole transaction; the next statement on it would only fail with
    /// <c>25P02 in_failed_sql_transaction</c>) and the retry unit is the caller's transaction.</summary>
    public int Attempts { get; } = attempts;
}
