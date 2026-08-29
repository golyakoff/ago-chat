using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.TransferConversation;

/// <summary>
/// `18-02`: hand a conversation from the operator who currently holds it to a named colleague, without
/// it ever leaving <see cref="ConversationState.Assigned"/>. <see cref="FromOperatorId"/> is the
/// caller's own claimed identity (the operator pressing "transfer"), checked against
/// <see cref="Conversation.OperatorId"/> by the handler, not carried on trust - see
/// <see cref="TransferConversationHandler"/>'s own remarks for why that check lives there rather than
/// in <see cref="Conversation.TransferTo"/>.
/// </summary>
public sealed record TransferConversation(
    ConversationId ConversationId, OperatorId FromOperatorId, OperatorId ToOperatorId, SiteId SiteId);
