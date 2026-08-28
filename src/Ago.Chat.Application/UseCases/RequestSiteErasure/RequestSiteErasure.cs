using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RequestSiteErasure;

public sealed record RequestSiteErasure(SiteId SiteId, OperatorId RequestedBy);
