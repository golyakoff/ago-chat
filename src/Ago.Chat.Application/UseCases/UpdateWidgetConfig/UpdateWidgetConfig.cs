using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.UpdateWidgetConfig;

/// <summary>
/// `11-01`. <see cref="PrimaryColorHex"/>/<see cref="Position"/> arrive as raw strings, not yet the
/// validated `Ago.Chat.Domain.WidgetConfig`/`Ago.Chat.Domain.Position` types - `UpdateWidgetConfigHandler`
/// is what validates both (its own remarks explain why that job belongs there, not the HTTP endpoint).
/// </summary>
public sealed record UpdateWidgetConfig(SiteId SiteId, OperatorId RequestedBy, string? PrimaryColorHex, string Position);
