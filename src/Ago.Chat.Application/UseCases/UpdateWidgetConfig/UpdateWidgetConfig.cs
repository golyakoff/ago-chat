using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.UpdateWidgetConfig;

/// <summary>
/// `11-01`. <see cref="PrimaryColorHex"/>/<see cref="Position"/> arrive as raw strings, not yet the
/// validated `Ago.Chat.Domain.WidgetConfig`/`Ago.Chat.Domain.Position` types - `UpdateWidgetConfigHandler`
/// is what validates both (its own remarks explain why that job belongs there, not the HTTP endpoint).
///
/// `11-10`: <see cref="Locale"/> joins on the same terms, a raw string `UpdateWidgetConfigHandler`
/// parses into `Ago.Chat.Domain.Locale` - one console form, one HTTP call, one command, even though
/// the handler ends up calling two separate `Site` methods with it (`Site.UpdateWidgetConfig` for
/// color/position, `Site.UpdateLocale` for this field), because `Locale` is not part of `WidgetConfig`
/// at the domain level (`SiteLocaleUpdated`'s own remarks).
///
/// `16-04`: <see cref="NoticeText"/>/<see cref="NoticeUrl"/> join as two more raw, unvalidated fields -
/// unlike <see cref="Locale"/>, both stay part of `Ago.Chat.Domain.WidgetConfig` itself
/// (`WidgetConfig`'s own remarks explain why), so they ride through the same
/// `new WidgetConfig(...)`/`Site.UpdateWidgetConfig` call color and position already use, with no third
/// `Site` method needed.
/// </summary>
public sealed record UpdateWidgetConfig(
    SiteId SiteId,
    OperatorId RequestedBy,
    string? PrimaryColorHex,
    string Position,
    string Locale,
    string? NoticeText,
    string? NoticeUrl);
