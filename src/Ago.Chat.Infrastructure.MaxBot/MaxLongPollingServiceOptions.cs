namespace Ago.Chat.Infrastructure.MaxBot;

/// <summary>`14-02`: the dev-only polling loop's own tuning - starting points, not measured numbers
/// (the same caveat `ChatModule.ConfigureChannelResilienceDefaults` carries for the resilience
/// thresholds; CLAUDE.md: "do not invent numbers... measure or stay silent").</summary>
public sealed class MaxLongPollingServiceOptions
{
    public const string SectionName = "Channels:Max:LongPolling";

    /// <summary>How often the outer loop re-reads which credentials are active - a newly registered or
    /// revoked bot takes up to this long to start or stop being polled. Not a tight bound: this is the
    /// dev-only loop, and a webhook (once a public HTTPS URL exists) reacts immediately regardless.</summary>
    public int CredentialRefreshIntervalSeconds { get; init; } = 30;

    /// <summary>How long to wait before retrying one bot's own poll loop after MAX's <c>GET /updates</c>
    /// throws - never zero, so a sustained outage on this path cannot spin-loop one HTTP request per
    /// tick per bot.</summary>
    public int ErrorBackoffSeconds { get; init; } = 5;
}
