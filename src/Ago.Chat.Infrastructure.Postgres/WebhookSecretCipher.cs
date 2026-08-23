using System.Security.Cryptography;
using System.Text;
using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// AES-256-GCM (BCL `System.Security.Cryptography.AesGcm`, no new package) - authenticated encryption,
/// so a tampered ciphertext fails to decrypt rather than silently producing garbage that would later
/// sign a webhook delivery with the wrong key. See `WebhookEndpoint`'s and `IWebhookSecretCipher`'s own
/// remarks for why this is reversible encryption, not a hash.
///
/// Layout: `nonce (12 bytes) || tag (16 bytes) || ciphertext`, one field, so the aggregate stores a
/// single opaque `byte[]` rather than three separate columns for a value nothing outside this class
/// ever needs to address by part.
/// </summary>
public sealed class WebhookSecretCipher : IWebhookSecretCipher
{
    private const int NonceLength = 12;
    private const int TagLength = 16;

    private readonly byte[] _key;

    public WebhookSecretCipher(WebhookSecretCipherOptions options)
    {
        if (string.IsNullOrEmpty(options.SecretEncryptionKey))
        {
            throw new InvalidOperationException(
                "Set Webhooks:SecretEncryptionKey - a base64-encoded 32-byte AES-256 key (infra-credentials, local-dev.md).");
        }

        _key = Convert.FromBase64String(options.SecretEncryptionKey);
        if (_key.Length != 32)
        {
            throw new InvalidOperationException(
                $"Webhooks:SecretEncryptionKey must decode to exactly 32 bytes for AES-256; got {_key.Length}.");
        }
    }

    public byte[] Encrypt(string secret)
    {
        var plaintext = Encoding.UTF8.GetBytes(secret);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];

        using var aesGcm = new AesGcm(_key, TagLength);
        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);

        var result = new byte[NonceLength + TagLength + ciphertext.Length];
        nonce.CopyTo(result, 0);
        tag.CopyTo(result, NonceLength);
        ciphertext.CopyTo(result, NonceLength + TagLength);
        return result;
    }

    public string Decrypt(byte[] ciphertext)
    {
        if (ciphertext.Length < NonceLength + TagLength)
        {
            throw new ArgumentException("Ciphertext is too short to contain a nonce and tag.", nameof(ciphertext));
        }

        var nonce = ciphertext.AsSpan(0, NonceLength);
        var tag = ciphertext.AsSpan(NonceLength, TagLength);
        var encrypted = ciphertext.AsSpan(NonceLength + TagLength);
        var plaintext = new byte[encrypted.Length];

        using var aesGcm = new AesGcm(_key, TagLength);
        aesGcm.Decrypt(nonce, encrypted, tag, plaintext);

        return Encoding.UTF8.GetString(plaintext);
    }
}
