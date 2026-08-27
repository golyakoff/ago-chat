using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// AES-256-GCM (BCL `System.Security.Cryptography.AesGcm`, no new package) - authenticated encryption,
/// so a tampered ciphertext fails to decrypt rather than silently producing garbage that would later
/// sign a webhook delivery with the wrong key. See `WebhookEndpoint`'s and `IWebhookSecretCipher`'s own
/// remarks for why this is reversible encryption, not a hash.
///
/// `14-02`: the byte-shuffling (nonce/tag layout, key-length validation) now lives in
/// <see cref="AesGcmCipher"/>, shared with <see cref="ChannelCredentialCipher"/> - see that class's own
/// remarks for why the two still keep separate keys despite sharing the primitive.
/// </summary>
public sealed class WebhookSecretCipher : IWebhookSecretCipher
{
    private readonly byte[] _key;

    public WebhookSecretCipher(WebhookSecretCipherOptions options)
    {
        if (string.IsNullOrEmpty(options.SecretEncryptionKey))
        {
            throw new InvalidOperationException(
                "Set Webhooks:SecretEncryptionKey - a base64-encoded 32-byte AES-256 key (infra-credentials, local-dev.md).");
        }

        _key = AesGcmCipher.ParseBase64Aes256Key(options.SecretEncryptionKey, "Webhooks:SecretEncryptionKey");
    }

    public byte[] Encrypt(string secret) => AesGcmCipher.Encrypt(_key, secret);

    public string Decrypt(byte[] ciphertext) => AesGcmCipher.Decrypt(_key, ciphertext);
}
