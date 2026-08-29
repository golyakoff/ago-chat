namespace Ago.Chat.Worker;

/// <summary>Bound from <c>MessageSearchIndexJob:*</c> config keys, matching
/// <c>PartitionMaintenanceJobOptions</c>'s own shape.</summary>
public sealed class MessageSearchIndexJobOptions
{
    public const string SectionName = "MessageSearchIndexJob";

    /// <summary>Hourly - shorter than <c>PartitionMaintenanceJobOptions.Interval</c>'s daily cadence
    /// on purpose: a partition that job creates ahead of need should not sit unsearchable for up to a
    /// full day before this job notices it. Every cycle beyond the first, for an already-indexed
    /// partition, is a cheap catalog lookup (<c>CreateIndexIfMissingAsync</c>'s own remarks) - running
    /// hourly costs nothing extra once the fleet has caught up.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);
}
