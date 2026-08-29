using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.DeleteTag;

public sealed record DeleteTag(SiteId SiteId, TagId TagId, OperatorId RequestedBy);
