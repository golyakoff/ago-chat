using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RenameTag;

public sealed record RenameTag(SiteId SiteId, TagId TagId, OperatorId RequestedBy, string Name);
