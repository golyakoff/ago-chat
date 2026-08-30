using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GenerateReplyDraft;

/// <summary>
/// `19-01`: operator-only by construction - there is deliberately no visitor entry point the way
/// `CreateAttachment`/`GetConversationHistory` have one, because a visitor requesting a draft of what
/// *they* should say next is not this item's scope (`adr/0078` kind 1 is a copilot for the operator
/// answering, not the visitor asking). One command, one handler method, not the "one handler, two
/// entry points" shape those use cases follow.
/// </summary>
public sealed record GenerateReplyDraftAsOperator(ConversationId ConversationId, OperatorId RequestedBy, SiteId SiteId);
