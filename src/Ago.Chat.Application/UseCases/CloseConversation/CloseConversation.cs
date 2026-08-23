using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.CloseConversation;

/// <summary>Operator-only - see <see cref="CloseConversationHandler"/>'s own remarks for why a
/// visitor has no path here. <see cref="SiteId"/> scopes the `conversation:close` permission check
/// (adr/0016) - it comes from the operator's own token claims, not a lookup, the same shape
/// `SendOperatorMessage` already uses.</summary>
public sealed record CloseConversation(ConversationId ConversationId, OperatorId OperatorId, SiteId SiteId);
