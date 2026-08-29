using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.CreateTag;

public sealed record CreateTag(SiteId SiteId, OperatorId RequestedBy, string Name);
