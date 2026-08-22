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

    /// <summary>Releases one previously-claimed slot. A no-op floor at zero - never goes negative,
    /// so a caller that races a duplicate release cannot corrupt the count.</summary>
    Task ReleaseAsync(OperatorId operatorId, CancellationToken cancellationToken);
}
