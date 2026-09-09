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
///
/// `24-05`: <see cref="RequireContactConsent"/> joins on the identical terms - already a plain
/// <see langword="bool"/> with nothing to validate, so it needs no parse step the way
/// <see cref="Position"/>/<see cref="Locale"/> do, and rides the same `new WidgetConfig(...)` call.
///
/// `23-63`: <see cref="AttractAttention"/> joins on the identical terms - one more plain
/// <see langword="bool"/> with nothing to validate, riding the same `new WidgetConfig(...)` call.
///
/// `23-64`: <see cref="AutoOpenEnabled"/> joins on the identical terms - one more plain
/// <see langword="bool"/>. <see cref="AutoOpenDelaySeconds"/> arrives as a raw <see langword="int"/>,
/// the same "not yet the validated Domain type" shape <see cref="Position"/> already has -
/// `UpdateWidgetConfigHandler` is what checks it is one of `AutoOpenDelay`'s six legal values, the
/// same enum-membership check that handler already runs for <see cref="Position"/>/<see cref="Locale"/>.
/// <see cref="AutoOpenGreetingText"/> joins on the identical terms `NoticeText`/`NoticeUrl` already
/// have - a raw, unvalidated string `Ago.Chat.Domain.WidgetConfig`'s own constructor validates.
/// </summary>
public sealed record UpdateWidgetConfig(
    SiteId SiteId,
    OperatorId RequestedBy,
    string? PrimaryColorHex,
    string Position,
    string Locale,
    string? NoticeText,
    string? NoticeUrl,
    bool RequireContactConsent = false,
    bool AttractAttention = false,
    bool AutoOpenEnabled = false,
    int AutoOpenDelaySeconds = 30,
    string? AutoOpenGreetingText = null);
