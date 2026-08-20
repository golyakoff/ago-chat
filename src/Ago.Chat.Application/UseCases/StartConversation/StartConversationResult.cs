using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.StartConversation;

public sealed record StartConversationResult(ConversationId ConversationId, bool IsNew);
