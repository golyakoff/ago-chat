using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetConversationOutcome;

/// <summary>`18-10`: the conversation detail panel's own read - the outcome currently recorded for one
/// conversation. Gated by <see cref="Permission.ConversationRead"/>, the same reasoning
/// `GetConversationTagsHandler`/`GetConversationNotesHandler` already give for their own reads: viewing
/// what is already recorded is not more sensitive than viewing the conversation itself, only changing it
/// is (<see cref="Permission.ConversationClose"/>, reused by
/// `SetConversationOutcomeHandler`).</summary>
public sealed record GetConversationOutcome(ConversationId ConversationId, SiteId SiteId, OperatorId RequestedBy);
