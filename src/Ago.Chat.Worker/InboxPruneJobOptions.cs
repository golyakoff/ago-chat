namespace Ago.Chat.Worker;

/// <summary>Bound from <c>InboxPruneJob:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule).</summary>
public sealed class InboxPruneJobOptions
{
    public const string SectionName = "InboxPruneJob";

    /// <summary>`15-04`'s scope: "whatever equivalent exists for inbox/idempotency rows... an
    /// idempotency table that never forgets is the same shape of problem." It does exist here - `inbox`
    /// has no writer that ever deletes a row (`messaging.md`: "every consumer records message_id...
    /// inside the same transaction as its work"). Its only purpose is deduplicating a redelivery within
    /// the broker's own retry window, which this system bounds tightly: `RetryPolicy.MaxAttempts` is 5
    /// everywhere it is configured, at a fixed 1-second retry-queue TTL
    /// (`Ago.Platform.Messaging.RabbitMq.RabbitMqEventConsumer`'s retry-queue `x-message-ttl`), so a
    /// message settles - delivered or dead-lettered - within roughly five seconds of its first
    /// delivery. 24 hours (matching <see cref="OutboxPruneJobOptions.RetentionWindow"/>, `outbox`'s own
    /// "consumer-side counterpart" per this item's own scope note) is therefore enormous headroom
    /// above the window a duplicate could plausibly still arrive in - the number is sized as a
    /// debugging aid ("did this message actually get deduplicated, or processed twice"), not to cover
    /// any real redelivery latency this system produces.</summary>
    public TimeSpan RetentionWindow { get; set; } = TimeSpan.FromHours(24);

    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(10);

    public int BatchSize { get; set; } = 1000;

    public int MaxBatchesPerCycle { get; set; } = 50;
}
