namespace Ago.Chat.Worker;

/// <summary>Bound from <c>ChannelDeliveryPruneJob:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule). Mirrors <see cref="WebhookDeliveryPruneJobOptions"/>
/// exactly - the item's own scope names that job as the precedent "down to the prune job".</summary>
public sealed class ChannelDeliveryPruneJobOptions
{
    public const string SectionName = "ChannelDeliveryPruneJob";

    /// <summary>`23-19`'s own scope: "its own window and its own prune job". Thirty days, matching
    /// <see cref="WebhookDeliveryPruneJobOptions.RetentionWindow"/>'s own reasoning exactly - "yesterday's
    /// failure" is the floor a tenant debugging a customer's "I never heard back" complaint needs, and
    /// this table is lower-volume still: one row per outbound *channel* message (a fraction of all
    /// traffic - the item's own Goal names channel conversations as the minority), never one per widget
    /// message. An operational default (CLAUDE.md: "do not invent numbers"), not a measured or
    /// product-committed figure.</summary>
    public TimeSpan RetentionWindow { get; set; } = TimeSpan.FromDays(30);

    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Matches <see cref="WebhookDeliveryPruneJobOptions.BatchSize"/>'s own reasoning - no
    /// per-row external I/O, so the statement's own footprint is the only cost.</summary>
    public int BatchSize { get; set; } = 1000;

    public int MaxBatchesPerCycle { get; set; } = 50;
}
