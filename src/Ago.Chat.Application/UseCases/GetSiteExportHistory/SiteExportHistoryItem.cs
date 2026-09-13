using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetSiteExportHistory;

/// <summary>
/// One row of the console's export-history table. <see cref="ExpiresAt"/> is populated only when
/// <see cref="Status"/> is <see cref="ExportStatus.Ready"/> - the one state with both a
/// <see cref="CompletedAt"/> to measure from and a stored object that <c>SiteExportPruneJob</c> will
/// actually delete. A <see cref="ExportStatus.Pending"/> row has no <see cref="CompletedAt"/> yet to
/// compute from; a <see cref="ExportStatus.Failed"/> row has a <see cref="CompletedAt"/> but never
/// produced an object, so there is nothing to auto-delete; a <see cref="ExportStatus.Expired"/> row's
/// object is already gone - its own <see cref="Status"/> is the answer, not a second, now-meaningless
/// date.
/// </summary>
public sealed record SiteExportHistoryItem(
    Guid ExportId,
    ExportStatus Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt,
    Uri? DownloadUrl,
    DateTimeOffset? ExpiresAt,
    string? FailureReason);
