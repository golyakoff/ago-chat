namespace Ago.Chat.Worker;

/// <summary>Bound from <c>PartitionMaintenanceJob:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule).</summary>
public sealed class PartitionMaintenanceJobOptions
{
    public const string SectionName = "PartitionMaintenanceJob";

    /// <summary>data-model.md: partitions are created ahead of time, never reactively - daily is
    /// frequent enough that a partition is never more than a day from existing before it is needed,
    /// against a monthly partition boundary.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromDays(1);

    /// <summary>How many months past the current one always have a partition ready. 2-06's backlog
    /// item: "current month plus the next two."</summary>
    public int MonthsAhead { get; set; } = 2;
}
