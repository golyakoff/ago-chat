namespace Ago.Chat.Infrastructure.YooKassa;

/// <summary>
/// `13-02`/`adr/0071` (webhook half superseded by `adr/0190`): bound from `Billing:YooKassa:*` - our own
/// fixed application credentials, not a per-tenant value (`adr/0071`'s own contrast with `adr/0024`'s
/// `WebhookEndpoint.SecretCiphertext`).
/// Read directly from `infra-credentials`/`docker/.env` the same way `Auth:Keycloak:Authority` already
/// is - never written to Postgres, no cipher, no new column.
///
/// <para><see cref="ShopId"/>/<see cref="SecretKey"/> are the one credential pair here, used by
/// <c>YooKassaPaymentsApiClient</c> to *call* ЮKassa's Payments API (HTTP Basic auth, shop id as the
/// username) - both to create/charge payments and, since `26-286`, to re-query a payment when a webhook
/// arrives. There is deliberately no webhook-verification key: ЮKassa does not sign its
/// console-configured HTTP notifications, so there is no shared secret to hold - the notification is
/// verified by re-querying the payment through this same Basic-auth credential and by an IP allowlist,
/// not by an HMAC (`26-286`/`adr/0190`, superseding `adr/0071`'s signature scheme). A missing/malformed credential
/// still fails host startup (`ChatModule`'s own `.Validate().ValidateOnStart()`), never the first real
/// checkout attempt.</para>
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
}
