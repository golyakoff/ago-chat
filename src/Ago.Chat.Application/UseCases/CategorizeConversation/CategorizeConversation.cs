using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.CategorizeConversation;

/// <summary>
/// `19-02`: one candidate `Ago.Chat.Worker.ConversationCategorizationJob` found - a closed, untagged
/// conversation past its own cutoff. Deliberately just the two ids, the same "nobody is acting on
/// anybody's behalf" shape `Application.UseCases.AutoCloseConversation.AutoCloseConversation`'s own
/// remarks give for the identical reason: no <c>OperatorId</c>, because this is a system-initiated
/// classification, not a request an operator made.
/// </summary>
public sealed record CategorizeConversation(ConversationId ConversationId, SiteId SiteId);
