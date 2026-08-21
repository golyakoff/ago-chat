using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.AssignConversation;

/// <summary>
/// Direct-claim by one operator - see the type-level remarks on <see cref="Domain.Conversation.AssignTo"/>
/// for what this is not (the Stage 4 assignment engine).
/// </summary>
public sealed record AssignConversation(ConversationId ConversationId, OperatorId OperatorId, SiteId SiteId);
