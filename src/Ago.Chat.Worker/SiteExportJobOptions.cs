namespace Ago.Chat.Worker;

/// <summary>Bound from <c>SiteExportJob:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule) - the same shape
/// <see cref="SiteErasureJobOptions"/> already establishes.</summary>
public sealed class SiteExportJobOptions
{
    public const string SectionName = "SiteExportJob";

    /// <summary>How often a sweep cycle runs. Shorter than <see cref="SiteErasureJobOptions.Interval"/>
    /// - an export has no drain-and-wait step to poll for (erasure's own reason for its 30s default),
    /// so there is nothing to gain from waiting longer between cycles; a tenant watching a progress
    /// spinner benefits from the shorter poll.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How many export requests one sweep cycle claims. Small and deliberately serial in
    /// effect - each request streams a tenant's full history to a local temp file and then uploads it,
    /// genuinely heavier per item than any other job in this file, so claiming a large batch would
    /// only mean more requests sitting mid-flight in one process at once rather than finishing any of
    /// them sooner.</summary>
    public int BatchSize { get; set; } = 2;

    /// <summary>Lifetime of the presigned PUT this job uses to upload the finished archive to object
    /// storage - the Worker's own internal transfer, unrelated to any client-facing presign
    /// (<see cref="AttachmentThumbnailGenerator"/>'s own <c>UrlLifetime</c> carries the identical
    /// "ephemeral, immediately-consumed" reasoning). Generous relative to that job's 2 minutes because
    /// a full-tenant archive can take meaningfully longer to build than a single thumbnail.</summary>
    public TimeSpan UploadUrlLifetime { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Lifetime of each presigned attachment-download URL embedded in the archive itself -
    /// distinct from <see cref="UploadUrlLifetime"/> (that one is consumed within this job's own
    /// process seconds after being minted; this one is read by whoever eventually opens the exported
    /// file, possibly much later). Set to seven days, the longest an AWS SigV4 presigned URL can
    /// express at all - not a measurement, the actual protocol ceiling - which is also the concrete
    /// shape of this item's own "the export decays" tradeoff (`16-03`'s Scope): a tenant who downloads
    /// the archive today and opens it next month gets working conversation and site data but dead
    /// attachment links.</summary>
    public TimeSpan AttachmentUrlLifetime { get; set; } = TimeSpan.FromDays(7);
}
