using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetAssignmentPenalty;

/// <summary>`23-05`: operator-authenticated, `site:configure`-gated (`adr/0016`) - the console's own
/// settings screen reading the site's current penalty. Deliberately uncached, the same reasoning
/// `GetOfflineAutoReply`'s own remarks give for its sibling read: this is a low-frequency admin read,
/// not the per-message path, and it is not the read the assignment claimers make either - theirs is a
/// raw, in-transaction query (`SiteAssignmentPenaltyQuery`, `Ago.Chat.Worker`), never this handler.</summary>
public sealed record GetAssignmentPenalty(SiteId SiteId, OperatorId RequestedBy);
