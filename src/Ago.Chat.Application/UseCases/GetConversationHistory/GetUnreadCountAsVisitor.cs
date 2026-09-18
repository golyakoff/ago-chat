using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetConversationHistory;

/// <summary>
/// `25-143`: the widget's own reload-time seed for its closed-launcher badge (`25-141`) - "how many
/// messages not authored by this visitor have landed since <paramref name="AfterSequence"/>", never a
/// page of the messages themselves. Same cursor direction as <see cref="GetConversationDeltaAsVisitor"/>
/// (forward, strictly after), and the same access question, but the answer is a count, not a page - see
/// <see cref="GetConversationHistoryHandler.HandleUnreadCountAsVisitorAsync"/>'s own remarks for why
/// that is a genuinely different read rather than <c>HandleDeltaAsVisitorAsync(...).Value.Count</c>.
/// </summary>
public sealed record GetUnreadCountAsVisitor(ConversationId ConversationId, VisitorId RequestedBy, int AfterSequence);
