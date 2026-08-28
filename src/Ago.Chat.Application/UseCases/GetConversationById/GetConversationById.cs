using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetConversationById;

public sealed record GetConversationById(ConversationId ConversationId, OperatorId RequestedBy, SiteId SiteId);
