using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.AcknowledgeMessageDelivered;

/// <summary>
/// `25-119`: the widget's own delivery ack - "this visitor's live connection actually received this
/// operator-authored message." <paramref name="VisitorId"/> comes from <c>VisitorHub</c>'s own
/// authenticated context (<c>Context.User!.GetVisitorId()</c>), never from the caller's own claim, the
/// same "identity from the token, never from the wire" shape every other visitor-scoped command in this
/// codebase already follows (<see cref="Application.UseCases.GetConversationHistory.GetConversationHistoryAsVisitor"/>'s
/// own <c>RequestedBy</c>).
/// </summary>
public sealed record AcknowledgeMessageDelivered(ConversationId ConversationId, VisitorId VisitorId, MessageId MessageId);
