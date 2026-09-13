using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetSiteExportHistory;

/// <summary>The console's own new screen: every export request this site has ever made. See
/// <see cref="GetSiteExportHistoryHandler"/> for why this is a separate query from
/// <see cref="Ago.Chat.Application.UseCases.GetSiteExportStatus.GetSiteExportStatus"/> rather than that
/// one widened to return a list.</summary>
public sealed record GetSiteExportHistory(SiteId SiteId, OperatorId RequestedBy);
