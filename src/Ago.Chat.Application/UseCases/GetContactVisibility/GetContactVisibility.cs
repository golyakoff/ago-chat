using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetContactVisibility;

/// <summary>`23-11`: the console's own settings-screen read of a site's current contact-visibility
/// rung - `site:configure`-gated (`adr/0016`), the same shape `GetAssignmentPenalty`'s own remarks
/// give for its sibling scalar setting. Deliberately uncached - see
/// <see cref="GetContactVisibilityHandler"/>'s own remarks.</summary>
public sealed record GetContactVisibility(SiteId SiteId, OperatorId RequestedBy);
