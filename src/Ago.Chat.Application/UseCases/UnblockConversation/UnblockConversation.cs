using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.UnblockConversation;

public sealed record UnblockConversation(ConversationId ConversationId, OperatorId RequestedBy, SiteId SiteId);
