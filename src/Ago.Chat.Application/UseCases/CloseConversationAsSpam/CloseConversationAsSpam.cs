using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.CloseConversationAsSpam;

/// <summary>`23-69`: the same shape <c>CloseConversation</c> already uses (Operator-only, <see
/// cref="SiteId"/> from the operator's own token claims, not a lookup) - see
/// <see cref="CloseConversationAsSpamHandler"/>'s own remarks for why this is a distinct command
/// rather than a boolean flag on the existing one.</summary>
public sealed record CloseConversationAsSpam(ConversationId ConversationId, OperatorId OperatorId, SiteId SiteId);
