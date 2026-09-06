using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.BlockConversation;

public sealed record BlockConversation(ConversationId ConversationId, OperatorId RequestedBy, SiteId SiteId);
