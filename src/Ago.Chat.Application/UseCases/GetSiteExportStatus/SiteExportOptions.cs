namespace Ago.Chat.Application.UseCases.GetSiteExportStatus;

/// <summary>
/// Bound from <c>SiteExport:*</c> config keys - the one setting the read side of export needs.
/// <see cref="DownloadUrlLifetime"/> governs the presigned GET <c>GetSiteExportStatusHandler</c> mints
/// fresh on every poll once a request is <c>Ready</c> (never stored - <c>export_requests.object_key</c>
/// is the durable fact, the URL is minted on demand), so it can stay short without the console ever
/// holding a URL that outlives its own poll. Deliberately uncached, unlike
/// <c>GetAttachmentDownloadUrlHandler</c>'s per-(attachment, viewer) cache: a status poll is a low-
/// frequency, one-per-tenant-per-export call, not the "render 20 attachments in a history" case that
/// cache exists to spare, so the extra moving part would not be earning its keep here.
/// </summary>
public sealed class SiteExportOptions
{
    public const string SectionName = "SiteExport";

    public TimeSpan DownloadUrlLifetime { get; set; } = TimeSpan.FromMinutes(15);
}
