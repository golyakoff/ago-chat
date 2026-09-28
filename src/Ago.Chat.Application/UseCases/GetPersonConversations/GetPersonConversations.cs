using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetPersonConversations;

/// <summary>`26-269`: the client-detail hub's own navigation question - "which of this person's
/// conversations do I open" - answered from nothing but a <see cref="PersonId"/>, the only handle a
/// client-list row carries (the calendar's `Contact` and chat's own `PersonProfileDto` both key on it,
/// never on a <see cref="ConversationId"/> the caller does not yet hold).</summary>
public sealed record GetPersonConversations(VisitorId PersonId, SiteId SiteId, OperatorId RequestedBy);
