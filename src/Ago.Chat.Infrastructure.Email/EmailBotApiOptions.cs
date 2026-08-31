namespace Ago.Chat.Infrastructure.Email;

/// <summary>
/// `14-09`: deployment-wide configuration for the email channel - App-wide, not per-tenant, the identical
/// shape <see cref="WebhookSecret"/>'s own remarks explain, mirroring
/// <c>Ago.Chat.Infrastructure.WhatsApp.WhatsAppBotApiOptions.AppSecret</c>/<c>.VerifyToken</c>.
///
/// <para><b>Why there is no per-tenant secret here at all, unlike every channel before this one.</b>
/// MAX/Telegram/VK/WhatsApp each connect one shop's own bot/community/number, entered by an operator
/// through a console connect endpoint and stored as a <see cref="Domain.ChannelCredential"/> row. Email has
/// nothing of that shape to store: `10-05` already built this deployment's own mail sending path (a single
/// self-hosted Postfix relay, no third-party provider), so there is no shop-supplied account to link -
/// every site shares the identical relay and the identical inbound pickup mechanism. What distinguishes one
/// site's email from another's is not a secret an operator enters; it is the address a visitor mails,
/// resolved structurally from <see cref="Domain.SiteId"/> via subaddressing (see
/// <see cref="EmailRecipientAddress"/>'s own remarks) - so this item ships **no console connect/disconnect
/// endpoint and no <see cref="Domain.ChannelCredential"/> row for this channel at all**, a deliberate,
/// named departure from every channel before it, not an oversight. `14-09`'s own Scope section, unlike
/// `14-08`'s/`14-10`'s, never asks for one either.</para>
///
/// <para><b>The consequence worth stating plainly: Email can only be turned on or off for the whole
/// deployment, not per site.</b> With <see cref="EmailBotApiOptions.Domain"/> unset, every site's email is unavailable
/// (<c>EmailWebhookEndpoints</c> refuses every delivery, <c>EmailChannelAdapter</c> refuses every send);
/// with it set, every site with a real, existing <see cref="Domain.SiteId"/> can receive email the moment a
/// visitor learns its own generated support address - there is no per-site opt-in step to add one, matching
/// this item's own reasoning that a per-site console step would have nothing genuine for an operator to
/// enter.</para>
///
/// <para><see cref="WebhookSecret"/> authenticates the one inbound HTTP boundary this item actually owns:
/// the JSON delivery from whatever process turns a raw SMTP transaction into an HTTP POST (this item's own
/// honesty note in <see cref="EmailInboundWebhookPayload"/> explains why that process is not built here).
/// Signed the identical way `6-03`'s outbound <c>X-Ago-Signature</c> and WhatsApp's inbound
/// <c>X-Hub-Signature-256</c> both are - HMAC-SHA256 over the raw request body, one shared key, not a
/// per-credential one, because (per above) there is no per-credential secret to key it with.</para>
/// </summary>
public sealed class EmailBotApiOptions
{
    public const string SectionName = "Channels:Email";

    /// <summary>The domain every generated support address and every outbound <c>From</c> address uses -
    /// `10-05`'s own sending domain in a real deployment (e.g. the zone that ADR's amendment gives SPF/
    /// DKIM/DMARC records for). Left unconfigured (empty) by default, the same "a deployment that has not
    /// configured this channel at all must still start" shape every other <c>*BotApiOptions</c> uses.</summary>
    public string Domain { get; init; } = string.Empty;

    /// <summary>The local part every generated support address shares, before the <c>+{siteId}</c>
    /// subaddress extension - <see cref="EmailRecipientAddress"/>'s own remarks on the full scheme.
    /// Defaults to the RFC 2142 alias `10-05` already provisioned on the self-hosted relay, so a real
    /// deployment needs no new alias for this item to start routing - only the domain's own
    /// <c>recipient_delimiter</c> configuration (Postfix's own setting, ago-deploy's own work, out of this
    /// item's scope) needs to exist for <c>+</c>-subaddressed mail to actually reach this system's inbound
    /// pickup at all.</summary>
    public string SupportLocalPart { get; init; } = "support";

    /// <summary>The SMTP relay <see cref="EmailSmtpClient"/> connects to - `10-05`'s own self-hosted
    /// Postfix in a real deployment, reachable with no authentication and no TLS
    /// (<see cref="EmailSmtpClient"/>'s own remarks explain why v1 builds neither). Defaults to
    /// <c>localhost:25</c> only so a deployment that has not configured this channel still starts; nothing
    /// sends until <see cref="Domain"/> is also set.</summary>
    public string SmtpHost { get; init; } = "localhost";

    public int SmtpPort { get; init; } = 25;

    /// <summary>The App-wide inbound webhook secret - see this type's own remarks on why one shared key,
    /// not a per-tenant one, is the correct shape here.</summary>
    public string? WebhookSecret { get; init; }
}
