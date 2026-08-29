using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.ListTags;

public sealed record ListTags(SiteId SiteId, OperatorId RequestedBy);
