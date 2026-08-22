using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetConversationHistory;

/// <summary>`3-03`: the reconnect case - every message strictly after <paramref
/// name="AfterSequence"/>, oldest first, as opposed to <see cref="GetConversationHistoryAsVisitor"/>'s
/// newest-first page.</summary>
public sealed record GetConversationDeltaAsVisitor(ConversationId ConversationId, VisitorId RequestedBy, int AfterSequence);
