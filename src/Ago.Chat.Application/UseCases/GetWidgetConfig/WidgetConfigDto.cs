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
///
/// `16-04`: <see cref="NoticeText"/>/<see cref="NoticeUrl"/> join as two more additive fields, straight
/// off `Ago.Chat.Domain.WidgetConfig` (unlike <see cref="Locale"/>, they need no second domain method to
/// read from - `WidgetConfig`'s own remarks explain why they stay part of that type).
/// </summary>
/// `24-05`: <see cref="RequireContactConsent"/> joins as one more additive field, straight off
/// `Ago.Chat.Domain.WidgetConfig` - the same "no second domain method to read from" shape
/// <see cref="NoticeText"/>/<see cref="NoticeUrl"/> already established for themselves.
/// `23-63`: <see cref="AttractAttention"/> joins on the identical terms - straight off
/// `Ago.Chat.Domain.WidgetConfig`, nothing to validate, no second domain method to read from.
public sealed record WidgetConfigDto(
    string? PrimaryColorHex, Position Position, Locale Locale, string? NoticeText, string? NoticeUrl,
    bool RequireContactConsent, bool AttractAttention);
