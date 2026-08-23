using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetVisitorPresence;

public sealed record GetVisitorPresence(ConversationId ConversationId, OperatorId RequestedBy, SiteId SiteId);
