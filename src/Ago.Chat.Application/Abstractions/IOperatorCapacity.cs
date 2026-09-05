using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `4-01`: the atomic compare-and-set capacity claim `concurrency.md`'s "Operator assignment -
/// the contended path" section specifies - not the <see cref="IConversationRepository"/> shape,
/// deliberately. A claim is <c>UPDATE operators SET active_chats = active_chats + 1 WHERE id = @id
/// AND active_chats &lt; capacity</c>: one round trip, compare and write together, no separate read
/// step for either side to race against. An EF load-mutate-save through an <c>Operator</c> aggregate
/// cannot express that as one statement without a second optimistic-concurrency collision to handle
/// on top - the same reasoning `adr/0004` already accepts for Dapper reads applies here to a write,
/// which is why this port exists beside <see cref="IConversationRepository"/> instead of extending
/// an aggregate that has no <c>ActiveChats</c> field to load and no reason to gain one.
///
/// <see cref="TryClaimAsync"/> returning <c>false</c> is "lost the race" or "no capacity left" - a
/// normal, expected outcome for every caller to treat as such (`concurrency.md`: "not an error to log
/// at <c>Error</c> level"), never an exception.
/// </summary>
public interface IOperatorCapacity
{
    /// <summary>Attempts to reserve one slot. <c>true</c> only if a slot was actually available and
    /// now reserved; <c>false</c> means nothing changed.</summary>
    Task<bool> TryClaimAsync(OperatorId operatorId, CancellationToken cancellationToken);

    /// <summary>
    /// `23-04`: reserves one slot unconditionally - <c>UPDATE operators SET active_chats =
    /// active_chats + 1 WHERE id = @id</c>, no <c>AND active_chats &lt; capacity</c> at all.
    /// <c>decisions.md</c> §2 is explicit that capacity gates the automatic assigner only, never a
    /// person's own deliberate choice: "a manual claim increments <c>active_chats</c> and does not
    /// check it. The counter rises past capacity freely." <c>active_chats</c> ending above
    /// <c>capacity</c> after this call is therefore the intended, correct outcome, not a bug for a
    /// future reader to "fix" back into <see cref="TryClaimAsync"/>'s own shape - see
    /// <c>AssignConversationHandler</c>'s own remarks for who is allowed to call this and why.
    ///
    /// <para><b>A second method, not a boolean parameter on <see cref="TryClaimAsync"/>.</b> One of the
    /// two can fail (a normal, expected outcome every caller of it must handle) and the other cannot -
    /// collapsing them into one method keyed by a <c>bool checkCapacity</c> would let a caller pass the
    /// wrong flag and silently receive the wrong guarantee, exactly the ambiguity CLAUDE.md's own
    /// backlog item text for this work called out by name. The method name carries the difference
    /// instead: <c>Try</c>-prefixed methods in this codebase return whether they succeeded,
    /// unprefixed ones do not because they cannot fail short of a real error.</para>
    ///
    /// <para><b>Still throws <see cref="OperatorCapacityContentionException"/> on a Postgres deadlock,
    /// exactly like <see cref="TryClaimAsync"/>.</b> "Cannot fail" describes the compare, not the
    /// statement - concurrency.md's lock-order section shows even a single-row <c>UPDATE</c> can lose a
    /// deadlock as an innocent bystander to the assignment engine's own multi-row batches, and rule 2
    /// forbids a raw <c>PostgresException</c> surfacing above Infrastructure regardless of which
    /// statement produced it.</para>
    /// </summary>
    Task ClaimAsync(OperatorId operatorId, CancellationToken cancellationToken);

    /// <summary>Releases one previously-claimed slot. A no-op floor at zero - never goes negative,
    /// so a caller that races a duplicate release cannot corrupt the count.
    ///
    /// <para>`6-10`: throws <see cref="OperatorCapacityContentionException"/>, never a raw
    /// <c>PostgresException</c>, when the store could not apply the decrement because it kept losing
    /// to concurrent writers on the same row. An implementation that owns its own transaction is
    /// expected to retry a bounded number of times before saying so; one running inside a
    /// caller-owned transaction cannot retry at all and says so on the first failure.</para></summary>
    Task ReleaseAsync(OperatorId operatorId, CancellationToken cancellationToken);
}
