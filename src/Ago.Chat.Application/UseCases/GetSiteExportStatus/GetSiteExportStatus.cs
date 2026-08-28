using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetSiteExportStatus;

public sealed record GetSiteExportStatus(Guid ExportId, SiteId SiteId, OperatorId RequestedBy);
