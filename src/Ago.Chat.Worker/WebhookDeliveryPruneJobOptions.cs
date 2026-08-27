namespace Ago.Chat.Worker;

/// <summary>Bound from <c>WebhookDeliveryPruneJob:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule).</summary>
public sealed class WebhookDeliveryPruneJobOptions
{
    public const string SectionName = "WebhookDeliveryPruneJob";

    /// <summary>`15-04`'s scope: keep the window `6-03`'s own support argument requires - "a webhook
    /// system without a delivery log is unsupportable," and pruning it has a floor, "it must stay
    /// useful to a tenant debugging yesterday's failure." Thirty days is chosen because "yesterday's
    /// failure" is the floor, not the target: a tenant who was away for a week, or whose CRM broke
    /// silently over a weekend, still needs to find the failed deliveries when they do look. This table
    /// is naturally much lower-volume than `outbox` - one row per delivery *attempt-summary*, and only
    /// for sites that have registered a webhook endpoint at all - so a month-long window does not carry
    /// the same growth risk. An operational default (CLAUDE.md: "do not invent numbers"), not a
    /// measured or product-committed figure.</summary>
    public TimeSpan RetentionWindow { get; set; } = TimeSpan.FromDays(30);

    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Matches <see cref="OutboxPruneJobOptions.BatchSize"/>'s own reasoning - no per-row
    /// external I/O, so the statement's own footprint is the only cost.</summary>
    public int BatchSize { get; set; } = 1000;

    public int MaxBatchesPerCycle { get; set; } = 50;
}
