namespace Ago.Chat.Contracts;

/// <summary>
/// `4-04`: an operator's last connection just dropped (`Ago.Chat.Api`'s
/// `OperatorHub.OnDisconnectedAsync`), or a periodic sweep found one already gone
/// (`OperatorDisconnectSweepJob`, `Ago.Chat.Worker`) - either way, a signal to start (or restart)
/// the grace-period wait, never proof the operator is gone for good. Published directly via
/// `IEventPublisher`, not through the outbox (`adr/0020`, the same shape as
/// `CacheInvalidationPublisher`): this describes no committed state change of its own, only a
/// presence observation derived from the connection registry - losing this publish is bounded by
/// the periodic sweep's own interval, not silent forever.
/// </summary>
public sealed record OperatorPresenceLost(Guid OperatorId, Guid SiteId, DateTimeOffset OccurredAt, Guid CorrelationId);
