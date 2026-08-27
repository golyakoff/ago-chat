using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetWidgetConfig;

/// <summary>
/// What `GetWidgetConfigHandler`/`UpdateWidgetConfigHandler` (`UseCases.UpdateWidgetConfig`, same
/// assembly) return - `Position` stays the typed Domain enum here, not a string; `Ago.Chat.Api`'s own
/// endpoint file is where that gets stringified for the wire (api-design.md's error/DTO boundary
/// convention: Application deals in typed values, only the HTTP edge serializes them), the same split
/// `ConversationSummaryDto.State` draws one layer further out.
///
/// `11-10`: <see cref="Locale"/> joins as a third, additive field on the same terms - the console's
/// widget-config screen reads and writes it through this one round trip rather than a second endpoint,
/// even though `Site.UpdateLocale` is its own domain method and raises its own event
/// (`UpdateWidgetConfigHandler`'s own remarks explain why one HTTP call can still call two domain
/// methods).
/// </summary>
public sealed record WidgetConfigDto(string? PrimaryColorHex, Position Position, Locale Locale);
