using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetWidgetConfig;

/// <summary>`11-01`: operator-authenticated, `site:configure`-gated (adr/0016) - see
/// <see cref="GetWidgetConfigHandler"/>'s own remarks for why this is a plain repository read, not
/// wrapped in `ICache.GetOrCreateAsync` the way the widget's own high-frequency handshake path is.
/// </summary>
public sealed record GetWidgetConfig(SiteId SiteId, OperatorId RequestedBy);
