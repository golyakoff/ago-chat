namespace Ago.Chat.Worker;

/// <summary>Bound from <c>AttachmentOrphanSweepJob:*</c> config keys, matching
/// <c>OperatorDisconnectSweepJobOptions</c>'s own shape (naming-and-structure.md's options-validation
/// rule). The expiry threshold itself is <em>not</em> here - it comes from
/// <c>AttachmentOptions.UploadLifetime</c>, the same value the presign step itself used
/// (`file-storage.md`'s Scope: "older than the presign lifetime"), so there is exactly one place that
/// value lives, never a second copy that could drift from it.</summary>
public sealed class AttachmentOrphanSweepJobOptions
{
    public const string SectionName = "AttachmentOrphanSweepJob";

    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);

    public int BatchSize { get; set; } = 100;
}
