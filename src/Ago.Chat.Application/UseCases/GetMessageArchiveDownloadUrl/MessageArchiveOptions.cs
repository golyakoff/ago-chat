namespace Ago.Chat.Application.UseCases.GetMessageArchiveDownloadUrl;

/// <summary>Bound from <c>MessageArchive:*</c> config keys - the read side's one setting, the same
/// shape and reasoning as <c>SiteExportOptions.DownloadUrlLifetime</c>: minted fresh on every request,
/// never stored, so it can stay short.</summary>
public sealed class MessageArchiveOptions
{
    public const string SectionName = "MessageArchive";

    public TimeSpan DownloadUrlLifetime { get; set; } = TimeSpan.FromMinutes(15);
}
