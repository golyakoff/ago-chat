namespace Ago.Chat.Infrastructure.WhatsApp;

/// <summary>
/// `14-10`: confirmed against Meta's own Cloud API documentation (developers.facebook.com, reachable
/// from this environment - unlike `14-02`'s MAX and `14-08`'s VK, both of which had to fall back to a
/// third-party write-up or an official SDK's source because their own primary documentation host was
/// unreachable here) - base URL <c>https://graph.facebook.com</c>, every call versioned with a
/// <c>/{version}</c> path segment (this item pins <c>v26.0</c>, the current stable version at the time
/// this item was built, 2026-08-30 - Meta's own changelog names a new version roughly every few months,
/// so a future reader should re-check rather than trust this value indefinitely), the access token
/// travels as a <c>Authorization: Bearer</c> header - a fourth genuinely distinct shape from every
/// precedent (MAX: header, no version segment; Telegram: token in the URL path itself; VK: an ordinary
/// POST/query parameter, no header).
///
/// <para><b><see cref="AppSecret"/> and <see cref="VerifyToken"/> are deployment configuration, not a
/// tenant's own secret - the central fact that makes WhatsApp's own routing shape genuinely different
/// from every precedent.</b> MAX/Telegram/Vk's inbound webhook is authenticated per credential
/// (<c>ChannelCredential.WebhookSecretHash</c>, a value this system generates fresh for each shop's own
/// bot/community at registration). WhatsApp's Cloud API has no equivalent: Meta's own "tech provider"
/// model (Embedded Signup) puts every onboarded client's inbound webhook behind <em>one</em> callback
/// URL and <em>one</em> signature key, both configured once against AGO's own Meta App - "all webhooks
/// for all of your onboarded business customers will be sent to your app's callback URL" (Meta's own
/// Embedded Signup overview, fetched 2026-08-30). <see cref="AppSecret"/> is the HMAC-SHA256 key Meta
/// signs every delivery with (<c>X-Hub-Signature-256</c>, Meta's own generic Graph API webhook
/// mechanism, not specific to WhatsApp); <see cref="VerifyToken"/> is the value AGO itself picks and
/// pastes into the Meta App Dashboard once, echoed back on the one-time <c>GET</c> verification
/// handshake (<c>WhatsAppWebhookEndpoints</c>' own remarks have the full handshake shape). Neither is
/// stored on <c>ChannelCredential</c> - see that type's own remarks (extended by this item) for why a
/// tenant-shaped row is the wrong home for an App-wide value.</para>
///
/// <para>Both are left nullable at the options level for the same reason
/// <c>VkBotApiOptions.PublicWebhookBaseUrl</c> is: a deployment that has not configured
/// <c>Channels:WhatsApp</c> at all must still start. <c>WhatsAppChannelEndpoints</c> refuses to let an
/// operator connect a number while either is unset (<see cref="Application.UseCases.ConversationErrors.ChannelNotAvailable"/>,
/// VK's own precedent for "there is genuinely nothing this deployment could do with a token yet"), and
/// <c>WhatsAppWebhookEndpoints</c> refuses every inbound delivery outright while <see cref="AppSecret"/>
/// is unset, since there would be nothing to verify a delivery's signature against - failing closed,
/// not open.</para>
/// </summary>
public sealed class WhatsAppBotApiOptions
{
    public const string SectionName = "Channels:WhatsApp";

    public string BaseUrl { get; init; } = "https://graph.facebook.com";

    public string ApiVersion { get; init; } = "v26.0";

    public string? AppSecret { get; init; }

    public string? VerifyToken { get; init; }
}
