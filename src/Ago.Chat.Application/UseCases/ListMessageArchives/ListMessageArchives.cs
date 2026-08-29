using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.ListMessageArchives;

public sealed record ListMessageArchives(SiteId SiteId, OperatorId RequestedBy);
