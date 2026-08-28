using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RequestConversationErasure;

public sealed record RequestConversationErasure(ConversationId ConversationId, OperatorId RequestedBy, SiteId SiteId);
