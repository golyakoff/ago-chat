namespace Ago.Chat.Worker;

/// <summary>Bound from <c>OutboxDispatcher:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule).</summary>
public sealed class OutboxDispatcherOptions
{
    public const string SectionName = "OutboxDispatcher";

    /// <summary>Fallback only - messaging.md: LISTEN/NOTIFY wakes the dispatcher immediately on a
    /// fresh row; this interval only matters for a missed or coalesced notification.
    ///
    /// <para><b>`23-89`'s own bound is built on this value.</b> Any grant-shaped write that rides the
    /// outbox (<c>ModuleQuantityGranted</c> included) has an expected dispatch-side wait of low
    /// single-digit seconds - <see cref="OutboxDispatcher"/> wakes on the notification itself, not on
    /// this timer - and a worst case, in a healthy system, of this <see cref="PollInterval"/> plus one
    /// <see cref="PublishTimeout"/> if the first publish attempt needed a retry: comfortably under a
    /// minute even with one hiccup. That bound holds only while the broker is reachable at all -
    /// <see cref="PublishTimeout"/>'s own remarks explain why an unreachable broker has no dispatch-side
    /// attempt cap, so a genuine outage is honestly unbounded until the broker recovers, not merely a
    /// longer version of the same number.</para></summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public int BatchSize { get; set; } = 20;

    /// <summary>resilience.md: "Timeout, retry with jittered backoff, publisher confirms" for the
    /// RabbitMQ/Kafka boundary - a publisher-confirmed publish against an unresponsive broker (paused,
    /// network-partitioned) waits for a confirm that will never come otherwise, blocking this whole
    /// batch forever instead of failing the one row and moving on.
    ///
    /// <para>`23-89`: this is also why a broker outage has no stated worst-case delay. A timed-out row
    /// is not retried on a fixed schedule - it simply stays unpublished until the next dispatch cycle
    /// (the next LISTEN/NOTIFY wake, or <see cref="PollInterval"/> as its own fallback) claims it
    /// again, indefinitely, for as long as the broker stays down. The outbox accumulates rather than
    /// gives up (resilience.md), which is correct, but it means "how long" has no honest upper bound
    /// in that state - only "until the broker is back", which is exactly what `23-89`'s own console
    /// text says instead of inventing a number rule 7 would forbid anyway.</para></summary>
    public TimeSpan PublishTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
