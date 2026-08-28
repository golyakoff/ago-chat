using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetSiteExportStatus;

/// <summary>The console's poll target: enough to drive "pending -> ready, here's your download" -
/// see `Ago.Chat.Api`'s `SitesEndpoints` for the wire shape this maps onto.</summary>
public sealed record SiteExportStatusItem(
    Guid ExportId,
    ExportStatus Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt,
    Uri? DownloadUrl,
    string? FailureReason);
