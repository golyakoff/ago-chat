namespace Ago.Chat.Worker;

/// <summary>Bound from <c>OutboxPruneJob:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule).</summary>
public sealed class OutboxPruneJobOptions
{
    public const string SectionName = "OutboxPruneJob";

    /// <summary>`15-04`'s scope: "a short operational window that is itself a debugging aid, short
    /// enough that the table stays small." Every published row is dispatched within seconds in steady
    /// state (LISTEN/NOTIFY - `OutboxDispatcher`'s own remarks), so the window's only real job is
    /// letting someone inspect a specific row after an incident, not covering normal processing
    /// latency. 24 hours is chosen to outlive `15-03`'s own alert cadence
    /// (`repeat_interval: 24h`) - an operator paged once about `OutboxLagGrowing` can still open this
    /// table the next day and see the rows that were in flight when the alert fired, before this job
    /// prunes them. An operational default, not a measurement (CLAUDE.md: "do not invent numbers");
    /// `outbox` carries no tier or tenant meaning to override it with, so unlike the `messages`
    /// retention horizon below this is not expected to become configuration `13-05` ever touches.</summary>
    public TimeSpan RetentionWindow { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How often a prune cycle runs. Frequent enough that `outbox` - a hot table, one row per
    /// message at minimum - never accumulates more than about ten minutes of newly-eligible rows
    /// between cycles, infrequent enough not to add needless load to the busiest table in the system.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Rows removed by one `DELETE ... LIMIT` statement - `15-04`'s own words: "a single
    /// unbounded DELETE on a hot table is its own incident." 1,000 is larger than
    /// `AttachmentOrphanSweepJobOptions.BatchSize` (100) because this delete does no per-row external
    /// I/O (no S3 call per row, unlike the attachment sweep) - the cost here is purely the statement's
    /// own lock/WAL footprint, which stays small at four figures on a 2Gi instance.</summary>
    public int BatchSize { get; set; } = 1000;

    /// <summary>A safety valve, not a target: bounds how many batches one cycle will issue before
    /// yielding to the next `Interval` tick, so a large first-run backlog (this job deployed onto a
    /// table nobody has ever pruned) cannot make one cycle run indefinitely. At the defaults this still
    /// drains up to 50,000 rows per cycle - comfortably ahead of any backlog this system's own traffic
    /// could produce between cycles once caught up.</summary>
    public int MaxBatchesPerCycle { get; set; } = 50;
}
