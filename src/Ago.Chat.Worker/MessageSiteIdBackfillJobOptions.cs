namespace Ago.Chat.Worker;

/// <summary>Bound from <c>MessageSiteIdBackfillJob:*</c> config keys, matching
/// <c>AttachmentOrphanSweepJobOptions</c>'s own shape (a `BatchSize` alongside an `Interval`).</summary>
public sealed class MessageSiteIdBackfillJobOptions
{
    public const string SectionName = "MessageSiteIdBackfillJob";

    /// <summary>Frequent relative to the maintenance jobs in this file's neighbourhood, because unlike
    /// them this one is not steady-state idle once it has converged - see this class's own remarks in
    /// <c>MessageSiteIdBackfillJob</c> for why a short interval here still costs nothing once every row
    /// has a `site_id`.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Rows updated per `UPDATE`, per partition, per cycle - the same "bounded batch, not one
    /// statement across the whole table" shape <c>AttachmentOrphanSweepJob</c> already uses for a
    /// different table this codebase did not want to lock in one pass.</summary>
    public int BatchSize { get; set; } = 500;
}
