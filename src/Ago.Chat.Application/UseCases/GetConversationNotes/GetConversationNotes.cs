using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetConversationNotes;

public sealed record GetConversationNotes(ConversationId ConversationId, SiteId SiteId, OperatorId RequestedBy);
