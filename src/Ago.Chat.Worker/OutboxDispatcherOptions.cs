namespace Ago.Chat.Worker;

/// <summary>Bound from <c>OutboxDispatcher:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule).</summary>
public sealed class OutboxDispatcherOptions
{
    public const string SectionName = "OutboxDispatcher";

    /// <summary>Fallback only - messaging.md: LISTEN/NOTIFY wakes the dispatcher immediately on a
    /// fresh row; this interval only matters for a missed or coalesced notification.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public int BatchSize { get; set; } = 20;

    /// <summary>resilience.md: "Timeout, retry with jittered backoff, publisher confirms" for the
    /// RabbitMQ/Kafka boundary - a publisher-confirmed publish against an unresponsive broker (paused,
    /// network-partitioned) waits for a confirm that will never come otherwise, blocking this whole
    /// batch forever instead of failing the one row and moving on.</summary>
    public TimeSpan PublishTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
