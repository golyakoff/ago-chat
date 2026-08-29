namespace Ago.Chat.Domain;

/// <summary>
/// `13-03`: raised by <see cref="Operator.Remove"/>, mapped to an outbox row in the same transaction as
/// <see cref="Operator.RemovedAt"/> itself (`Ago.Chat.Application.Mapping.OperatorRemovedMapper`) -
/// this is a real committed state change (CLAUDE.md rule 4: state change and integration event, one
/// transaction), not a presence observation like `OperatorPresenceLost` (that one is published directly,
/// never through the outbox, because it describes no committed state of its own). `Ago.Chat.Worker`'s
/// own consumer is what actually releases this operator's `Assigned` conversations back to `Waiting`,
/// out of this request's transaction.
/// </summary>
public sealed record OperatorRemoved(OperatorId OperatorId, SiteId SiteId, DateTimeOffset OccurredAt) : IDomainEvent;
