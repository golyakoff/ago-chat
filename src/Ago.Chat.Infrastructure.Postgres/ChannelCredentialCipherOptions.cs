namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `Channels:CredentialEncryptionKey` - a base64-encoded 32-byte AES-256 key,
/// `adr/0069`'s named entry in `17-03`'s secrets inventory. Bound from the same `infra-credentials`
/// mechanism `Webhooks:SecretEncryptionKey`/`Auth:SigningKey` already use (`docker/.env`, gitignored,
/// never committed - `repositories.md`'s "no secrets, ever").
///
/// <para><b>Rotation is Draining, the same class `17-03`/`adr/0067` moved the visitor signing key to</b>:
/// generate a new key, decrypt every <c>channel_credentials.token_ciphertext</c> row with the old key
/// and re-encrypt with the new one in one pass (there is no "old and new both valid" trick for a
/// symmetric cipher the way there is for a signing key ring - a ciphertext was made with exactly one
/// key), then retire the old key once every row is migrated. Until that migration tool exists, this key
/// is <b>Breaking</b> in practice, the same open-finding shape `secrets.md` records for
/// `Webhooks:SecretEncryptionKey` today - named here rather than silently assumed solved, so a reader
/// checking this file against `secrets.md` finds the same honest answer in both places.</para>
///
/// <para>Deliberately <b>no</b> random-per-process fallback - `WebhookSecretCipherOptions`'s own
/// reasoning applies unchanged: a lost key here makes every already-registered channel token
/// permanently unrecoverable, silently, so this fails fast instead of guessing.</para>
/// </summary>
public sealed class ChannelCredentialCipherOptions
{
    public const string SectionName = "Channels";

    public string CredentialEncryptionKey { get; init; } = string.Empty;
}
