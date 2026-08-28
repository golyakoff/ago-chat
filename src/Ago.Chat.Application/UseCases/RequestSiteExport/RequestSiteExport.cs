using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RequestSiteExport;

public sealed record RequestSiteExport(SiteId SiteId, OperatorId RequestedBy);
