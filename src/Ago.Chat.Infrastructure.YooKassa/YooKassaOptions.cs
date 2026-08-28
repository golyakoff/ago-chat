namespace Ago.Chat.Infrastructure.YooKassa;

/// <summary>
/// `13-02`/`adr/0025`: bound from `Billing:YooKassa:*` - our own fixed application credentials, not a
/// per-tenant value (`adr/0025`'s own contrast with `adr/0024`'s `WebhookEndpoint.SecretCiphertext`).
/// Read directly from `infra-credentials`/`docker/.env` the same way `Auth:Keycloak:Authority` already
/// is - never written to Postgres, no cipher, no new column.
///
/// <para><see cref="ShopId"/>/<see cref="SecretKey"/> are used by <c>YooKassaPaymentsApiClient</c> to
/// *call* ЮKassa's Payments API (HTTP Basic auth, shop id as the username); <see cref="WebhookKey"/> is
/// used by <c>YooKassaWebhookSignatureVerifier</c> to *verify* inbound notifications - two different
/// keys with two different directions of trust, both required (`ChatModule`'s own
/// `.Validate().ValidateOnStart()`), matching `adr/0024`'s `Webhooks:SecretEncryptionKey` precedent: a
/// missing/malformed credential fails host startup, never the first real checkout attempt.</para>
/// </summary>
public sealed class YooKassaOptions
{
    public const string SectionName = "Billing:YooKassa";

    /// <summary>ЮKassa's own documented Payments API base - a public, well-known URL, not a secret,
    /// hence the real default (the same "hardcode the provider's real base URL, let options override it
    /// for a test's own fake host" shape <c>MaxBotApiOptions.BaseUrl</c>/<c>TelegramBotApiOptions.BaseUrl</c>
    /// already establish).</summary>
    public string BaseUrl { get; set; } = "https://api.yookassa.ru/v3/";

    public string ShopId { get; set; } = string.Empty;

    public string SecretKey { get; set; } = string.Empty;

    public string WebhookKey { get; set; } = string.Empty;
}
