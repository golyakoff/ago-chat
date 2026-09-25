using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Module;

/// <summary>
/// `4-04`: publishes `OperatorPresenceLost` directly via `IEventPublisher`, not through the outbox -
/// the same shape as `CacheInvalidationPublisher` (`adr/0020`): a presence observation describes no
/// committed state change of its own, so there is nothing to stage in the same transaction as.
///
/// `26-108`: this is the one chokepoint both callers share - `OperatorHub.OnDisconnectedAsync`'s
/// fast path and `OperatorDisconnectSweepJob`'s periodic backstop - so it is also the one place a
/// per-operator suppression marker can dedup them, rather than teaching each caller its own copy of
/// the rule. Before `26-108` the sweep re-published a Lost for the same still-disconnected,
/// still-`Assigned` operator on every 15s tick for as long as the grace consumer took to release
/// them (>= its 30s `GracePeriod`), and each duplicate held one more delivery unacked on
/// `operator-disconnect-grace` at prefetch 50 - a queue-amplification bug, not a correctness one:
/// `OperatorDisconnectGraceConsumer`'s own `ReleaseAllAsync` already treats a redelivered/duplicate
/// Lost as a harmless no-op.
///
/// The marker reuses `IRateLimiter` (`Ago.Platform.Caching.Redis`'s `RedisRateLimiter`, already
/// registered for every host by `ChatModule`'s own `AddRedisCaching` call - see that project's own
/// remarks) rather than a new port or a raw `IConnectionMultiplexer` reference here: a `RateLimitRule`
/// with `Capacity: 1` and a `RefillPerSecond` tuned so exactly one token refills after
/// `OperatorPresenceLostSuppressionOptions.Ttl` gives the same atomic, cross-replica,
/// single-Lua-round-trip claim a `SET key value NX EX ttl` would - the first check for a key finds a
/// full bucket (`Allowed: true`, consumes the only token), every check inside the TTL finds an empty
/// bucket (`Allowed: false`), and one token refills after the TTL, which is exactly "claim once per
/// window" (`caching.md`'s own "Rate limiting and counters" section). This needed no new
/// Infrastructure project, and Domain/Application/Module still never see `IConnectionMultiplexer` -
/// the same dependency rule every other external resource in this codebase already follows
/// (`clean-architecture.md`).
///
/// A failed check (Redis unreachable) fails open (`RedisRateLimiter`'s own documented posture,
/// `caching.md`) - this dedup is an optimisation on top of an already-idempotent release, so the
/// safe failure mode is "publish anyway," i.e. degrade back to the pre-`26-108` behaviour, never
/// "silently drop a genuine Lost."
/// </summary>
public sealed class OperatorPresencePublisher(
    IEventPublisher publisher,
    IClock clock,
    IIdGenerator idGenerator,
    IRateLimiter rateLimiter,
    OperatorPresenceLostSuppressionOptions suppressionOptions)
{
    public async Task PublishLostAsync(OperatorId operatorId, SiteId siteId, CancellationToken cancellationToken)
    {
        var claim = await rateLimiter.CheckAsync(
            new RateLimitKey($"operator-presence-lost:{operatorId.Value}"),
            new RateLimitRule(Capacity: 1, RefillPerSecond: 1.0 / suppressionOptions.Ttl.TotalSeconds),
            cancellationToken);
        if (!claim.Allowed)
        {
            // `26-108`: a Lost for this operator was already claimed within the TTL window above -
            // by the other caller, or by an earlier sweep tick for the same still-gone operator.
            // Skip - the grace consumer is already handling it (see this class's own remarks).
            return;
        }

        var now = clock.UtcNow;
        var contract = new OperatorPresenceLost(operatorId.Value, siteId.Value, now, idGenerator.NewId(now));

        var envelope = new EventEnvelope(
            MessageId: idGenerator.NewId(now),
            Type: nameof(OperatorPresenceLost),
            Version: 1,
            PartitionKey: operatorId.Value.ToString(),
            OccurredAt: now,
            CorrelationId: contract.CorrelationId,
            Payload: JsonSerializer.Serialize(contract));

        await publisher.PublishAsync(envelope, cancellationToken);
    }
}
