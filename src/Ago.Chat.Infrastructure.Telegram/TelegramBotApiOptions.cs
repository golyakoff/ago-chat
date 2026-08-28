namespace Ago.Chat.Infrastructure.Telegram;

/// <summary>
/// `14-07`: confirmed against Telegram's own public Bot API documentation (core.telegram.org/bots/api),
/// 2026-08-28 - base URL <c>https://api.telegram.org</c>, and every method is addressed as
/// <c>{BaseUrl}/bot&lt;token&gt;/{method}</c> (e.g. <c>POST /bot&lt;token&gt;/sendMessage</c>,
/// <c>GET /bot&lt;token&gt;/getUpdates</c>) - the token travels <b>in the URL path</b>, not in an
/// <c>Authorization</c> header the way MAX's own API works
/// (<c>Ago.Chat.Infrastructure.MaxBot.MaxBotApiOptions</c>' own remarks). That is a genuine,
/// non-cosmetic divergence from the MAX pattern this item was otherwise modelled on, not an oversight -
/// <see cref="TelegramApiClient"/> builds every request path from the token accordingly, and never adds
/// an <c>Authorization</c> header at all.
///
/// <para><b>There is deliberately no webhook option here, unlike <c>MaxBotApiOptions.PublicWebhookBaseUrl</c>.</b>
/// MAX ships both a long-polling loop and a webhook receiver because MAX's own documentation calls
/// webhook the production mechanism and long polling development-only. Telegram's own documentation
/// makes no such claim - long polling is an entirely supported, unlimited-duration production mechanism
/// for Telegram, the only real difference being that a webhook additionally lets Telegram push updates
/// without this process holding an open poll. This item does not build a webhook receiver at all: this
/// deployment's own <c>adr/0070</c> (in <c>ago-root</c>) measured and fixed only the <em>outbound</em>
/// direction (this VPS calling Telegram through a SOCKS5 relay - see <see cref="TelegramProxyOptions"/>);
/// inbound webhook reachability (Telegram calling back into this VPS) was never measured, and the relay
/// does nothing for that direction. So <see cref="TelegramLongPollingService"/> is this channel's one and
/// only real, permanent production inbound path - not a dev-only fallback the way MAX's own poller is -
/// and that class's own doc comment says so explicitly.</para>
/// </summary>
public sealed class TelegramBotApiOptions
{
    public const string SectionName = "Channels:Telegram";

    public string BaseUrl { get; init; } = "https://api.telegram.org";

    /// <summary>How long a long-poll <c>GET /bot&lt;token&gt;/getUpdates</c> request may block waiting
    /// for a new update before returning an empty result - Telegram's own long-polling parameter, not
    /// this process's HTTP timeout (<see cref="TelegramLongPollingServiceOptions"/> owns that one,
    /// mirroring the same split <c>MaxBotApiOptions.LongPollTimeoutSeconds</c> draws next to
    /// <c>MaxLongPollingServiceOptions</c>). Telegram's own documentation recommends a value comfortably
    /// under a minute so a caller can still notice a stalled connection; 30 is this item's own starting
    /// point, not a measured number (CLAUDE.md: "do not invent numbers... measure or stay silent").</summary>
    public int LongPollTimeoutSeconds { get; init; } = 30;
}
