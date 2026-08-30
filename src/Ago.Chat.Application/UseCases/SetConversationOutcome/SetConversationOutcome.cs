using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.SetConversationOutcome;

/// <summary>
/// `18-10`: <paramref name="Outcome"/> is the wire string, not yet a <see cref="ConversationOutcome"/> -
/// parsed and validated inside <see cref="SetConversationOutcomeHandler"/>, the same "the command
/// carries the raw value, the handler translates it at the Application boundary" split
/// `UpdateWidgetConfig` already uses for <c>Locale</c>/<c>Position</c>.
///
/// Gated on <see cref="Permission.ConversationClose"/>, not a new permission - the backlog item's own
/// Scope: "an operator who can close a conversation can record what it led to." Unlike
/// <c>CloseConversation</c>, there is no "must be the currently assigned operator" check layered on top
/// of the permission (`SetConversationOutcomeHandler`'s own remarks explain why): recording an outcome
/// is closer to `TagConversation`'s shape (a labelling action any permitted operator on the site may
/// take) than to `Close`'s shape (a state transition scoped to whoever is actually handling the
/// conversation right now).
/// </summary>
public sealed record SetConversationOutcome(
    ConversationId ConversationId, SiteId SiteId, OperatorId RequestedBy, string Outcome);
