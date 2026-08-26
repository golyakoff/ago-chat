using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetOfflineAutoReply;

/// <summary>`14-04`: operator-authenticated, `site:configure`-gated (`adr/0016`) - the console's own
/// settings screen reading what is currently configured. Deliberately uncached, for exactly the
/// reason <c>GetWidgetConfigHandler</c> states for its own sibling read: this is a low-frequency admin
/// read, not the per-message path, and the cached copy is the one
/// <c>GetSiteConfigByIdHandler</c> serves.</summary>
public sealed record GetOfflineAutoReply(SiteId SiteId, OperatorId RequestedBy);
