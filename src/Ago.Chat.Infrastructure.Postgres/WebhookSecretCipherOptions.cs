namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `Webhooks:SecretEncryptionKey` - a base64-encoded 32-byte AES-256 key. Bound from the same
/// `infra-credentials` mechanism `Auth:SigningKey`/the Postgres and RabbitMQ passwords already use
/// (`docker/.env`, gitignored, never committed - `repositories.md`'s "no secrets, ever").
///
/// Deliberately <b>no</b> random-per-process fallback, unlike `Program.cs`'s JWT signing key: a lost
/// signing key only invalidates outstanding tokens (visible, recoverable - a caller just logs in
/// again); a lost encryption key makes every already-registered webhook secret permanently
/// unrecoverable (silent, unrecoverable - the tenant would need to revoke and re-register every
/// endpoint with no warning this happened). This fails fast instead, the same "fail fast" treatment
/// `AGO_CHAT_CONNECTION_STRING`/`Auth:Keycloak:Authority` already get for the analogous reason.
/// </summary>
public sealed class WebhookSecretCipherOptions
{
    public const string SectionName = "Webhooks";

    public string SecretEncryptionKey { get; init; } = string.Empty;
}
