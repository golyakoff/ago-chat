using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetCannedResponses;

/// <summary>`18-03`: operator-authenticated, `site:configure`-gated (`adr/0016`) - the console's own
/// settings screen *and* the composer's picker both read through this handler. Deliberately uncached,
/// the same reason `GetOfflineAutoReplyHandler` states for its own sibling read: this is a
/// low-frequency admin read compared to the per-message path, not the per-message path itself - see
/// `Site.UpdateCannedResponses`'s own remarks for why that is also why the write raises no cache-
/// invalidation event.</summary>
public sealed record GetCannedResponses(SiteId SiteId, OperatorId RequestedBy);
