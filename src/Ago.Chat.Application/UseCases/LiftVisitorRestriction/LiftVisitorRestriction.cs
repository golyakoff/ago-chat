using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.LiftVisitorRestriction;

/// <summary>`23-69`/`23-77`: addressed by <see cref="VisitorId"/> directly, not by a conversation - the
/// one write in this item's own scope that genuinely has no conversation to hang off: the console's
/// own restrictions screen (both items' own "the tenant can see... and reverse it" requirement) lists
/// visitors, not conversations, and an operator lifting a restriction from there may have no particular
/// conversation open at all.</summary>
public sealed record LiftVisitorRestriction(VisitorId VisitorId, OperatorId OperatorId, SiteId SiteId);
