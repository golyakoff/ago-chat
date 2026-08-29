namespace Ago.Chat.Worker;

/// <summary>Bound from <c>MessageArchiveJob:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule) - the same shape <see cref="SiteExportJobOptions"/>
/// already establishes for the identical "build an archive, upload it" job pattern.</summary>
public sealed class MessageArchiveJobOptions
{
    public const string SectionName = "MessageArchiveJob";

    /// <summary>How often a cycle runs. Daily, matching <see cref="MessagePartitionPruneJobOptions.Interval"/> -
    /// this job's whole purpose is staying ahead of that one's own drop decision, so there is nothing to
    /// gain from checking more often than the job it is racing to stay ahead of.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Lifetime of the presigned PUT this job uses to upload one finished archive - the
    /// Worker's own internal transfer, the same "ephemeral, immediately-consumed" reasoning
    /// <see cref="SiteExportJobOptions.UploadUrlLifetime"/> already gives.</summary>
    public TimeSpan UploadUrlLifetime { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Lifetime of each presigned attachment-download URL embedded in the archive itself -
    /// the same field, same reasoning, and the same protocol-ceiling value as
    /// <see cref="SiteExportJobOptions.AttachmentUrlLifetime"/>. Honestly shorter-lived in practice
    /// than that field's own promise: <c>AttachmentRetentionSweepJob</c> deletes the underlying object
    /// once this same partition is confirmed archived and dropped, which - unlike a live tenant's own
    /// export - can happen well inside this URL's own week-long signed lifetime. A reader of an old
    /// retention archive gets a working link only for the (usually short) window between this job
    /// archiving a period and that one sweeping its attachments; `13-06`'s own report states this
    /// plainly as a real, deliberate limitation, not a bug.</summary>
    public TimeSpan AttachmentUrlLifetime { get; set; } = TimeSpan.FromDays(7);
}
