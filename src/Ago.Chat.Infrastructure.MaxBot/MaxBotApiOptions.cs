namespace Ago.Chat.Infrastructure.MaxBot;

/// <summary>
/// `14-02`: confirmed against MAX's own documentation, 2026-08-27 (this item's backlog note) - base URL
/// <c>https://platform-api2.max.ru</c>, the token travels in the <c>Authorization</c> header, 30 rps per
/// bot.
///
/// <para><see cref="PublicWebhookBaseUrl"/> is the switch between this item's two inbound mechanisms,
/// both required because MAX's own documentation calls long polling development-only and webhook the
/// production mechanism (this item's backlog note, "state which MAX's actual API requires and place it
/// there accordingly" resolves to both). When set, <c>MaxChannelEndpoints</c> calls MAX's own
/// <c>POST /subscriptions</c> at registration time, pointing MAX at
/// <c>{PublicWebhookBaseUrl}/webhooks/max/{credentialId}</c>. When unset (the local compose loop, which
/// has no public HTTPS endpoint with a trusted-CA certificate - MAX has refused plain HTTP and
/// self-signed certificates since 2026-05-25), registration skips the MAX-side call entirely and
/// <c>Ago.Chat.Worker</c>'s <see cref="MaxLongPollingService"/> is what actually receives messages.</para>
/// </summary>
public sealed class MaxBotApiOptions
{
    public const string SectionName = "Channels:Max";

    public string BaseUrl { get; init; } = "https://platform-api2.max.ru";

    public Uri? PublicWebhookBaseUrl { get; init; }

    /// <summary>How long a long-poll <c>GET /updates</c> request may block waiting for a new update
    /// before returning empty - MAX's own long-polling parameter, not this process's HTTP timeout
    /// (<see cref="MaxLongPollingServiceOptions"/> owns that one, the same "transport timeout is a
    /// distinct concern from the provider's own polling window" split `Ago.Chat.Webhooks`'
    /// <c>WebhookHttpOptions</c> already draws next to its resilience pipeline's own timeout).</summary>
    public int LongPollTimeoutSeconds { get; init; } = 25;
}
