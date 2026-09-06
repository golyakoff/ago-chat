using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RevealVisitorContactDetail;

/// <summary>
/// `23-11`/`decisions.md` §5: one contact detail, one reveal - gated exactly as
/// `ListVisitorContactDetails` is (<see cref="Permission.ConversationRead"/>) and scoped by site the
/// same way, regardless of the site's own <see cref="ContactVisibility"/> rung. A reveal is always
/// available to whoever can already read the conversation - the rung decides only whether the *list*
/// read shows the real value or asks for a reveal first; it is not a second gate this endpoint checks
/// (`RevealVisitorContactDetailHandler`'s own remarks).
/// </summary>
public sealed record RevealVisitorContactDetail(
    ConversationId ConversationId, VisitorContactDetailId ContactDetailId, OperatorId RequestedBy, SiteId SiteId);
