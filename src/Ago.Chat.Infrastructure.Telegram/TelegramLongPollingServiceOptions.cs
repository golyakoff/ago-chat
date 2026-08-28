namespace Ago.Chat.Infrastructure.Telegram;

/// <summary>`14-07`: this loop's own tuning - starting points, not measured numbers (the same caveat
/// <c>MaxLongPollingServiceOptions</c> and <c>ChatModule.ConfigureChannelResilienceDefaults</c> carry;
/// CLAUDE.md: "do not invent numbers... measure or stay silent"). Unlike
/// <c>MaxLongPollingServiceOptions</c>, this is not a dev-only loop's tuning - see
/// <see cref="TelegramLongPollingService"/>'s own remarks on why this channel has no separate production
/// mechanism to fall back on.</summary>
public sealed class TelegramLongPollingServiceOptions
{
    public const string SectionName = "Channels:Telegram:LongPolling";

    /// <summary>How often the outer loop re-reads which credentials are active - a newly registered or
    /// revoked bot takes up to this long to start or stop being polled.</summary>
    public int CredentialRefreshIntervalSeconds { get; init; } = 30;

    /// <summary>How long to wait before retrying one bot's own poll loop after Telegram's
    /// <c>GET /getUpdates</c> throws - never zero, so a sustained outage on this path cannot spin-loop
    /// one HTTP request per tick per bot.</summary>
    public int ErrorBackoffSeconds { get; init; } = 5;
}
