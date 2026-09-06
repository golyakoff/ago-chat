using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.SendTeamMessage;

/// <summary>
/// `23-32`: <see cref="SiteId"/> comes from the operator's own token claims, never a caller-supplied
/// value or a lookup - the same shape <c>SendOperatorMessage</c>'s own doc comment states for itself.
/// Unlike an ordinary conversation message, there is no permission gate on *whether* an operator may
/// post here at all: "every operator of that tenant in it" (the backlog item's own Scope) is
/// unconditional, so the only per-request decision this handler makes is *how* the sender is
/// labelled, not *whether* they may send.
/// </summary>
public sealed record SendTeamMessage(SiteId SiteId, OperatorId AuthorId, string Body, Guid? ClientMessageId = null);
