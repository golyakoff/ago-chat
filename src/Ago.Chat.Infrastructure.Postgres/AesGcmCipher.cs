using System.Security.Cryptography;
using System.Text;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `14-02`: the AES-256-GCM primitive <see cref="WebhookSecretCipher"/> already established, pulled out
/// so <see cref="ChannelCredentialCipher"/> can reuse the identical, already-proven byte layout
/// (`nonce (12 bytes) || tag (16 bytes) || ciphertext`) under a *different* key rather than
/// hand-rolling a second implementation of the same fifteen lines. `adr/0069` names why the key itself
/// still must not be shared between the two callers even though the algorithm is: the callers protect
/// two different secrets that must be able to rotate on two different schedules.
///
/// <para>Extracted rather than left duplicated the moment a second caller existed - the "rule of two"
/// reasonable exception to waiting for a third, since the two implementations were already
/// byte-for-byte identical and a real reviewer would flag the copy-paste immediately. Both
/// <see cref="WebhookSecretCipher"/> and <see cref="ChannelCredentialCipher"/> keep their own key,
/// their own <c>options</c> validation and their own port (<c>IWebhookSecretCipher</c> /
/// <c>IChannelCredentialCipher</c>) - only the byte-shuffling is shared, which is the part that had
/// zero behavioural difference between the two to begin with.</para>
/// </summary>
internal static class AesGcmCipher
{
    private const int NonceLength = 12;
    private const int TagLength = 16;

    public static byte[] Encrypt(byte[] key, string plaintext)
    {
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagLength];

        using var aesGcm = new AesGcm(key, TagLength);
        aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        var result = new byte[NonceLength + TagLength + ciphertext.Length];
        nonce.CopyTo(result, 0);
        tag.CopyTo(result, NonceLength);
        ciphertext.CopyTo(result, NonceLength + TagLength);
        return result;
    }

    public static string Decrypt(byte[] key, byte[] ciphertext)
    {
        if (ciphertext.Length < NonceLength + TagLength)
        {
            throw new ArgumentException("Ciphertext is too short to contain a nonce and tag.", nameof(ciphertext));
        }

        var nonce = ciphertext.AsSpan(0, NonceLength);
        var tag = ciphertext.AsSpan(NonceLength, TagLength);
        var encrypted = ciphertext.AsSpan(NonceLength + TagLength);
        var plaintext = new byte[encrypted.Length];

        using var aesGcm = new AesGcm(key, TagLength);
        aesGcm.Decrypt(nonce, encrypted, tag, plaintext);

        return Encoding.UTF8.GetString(plaintext);
    }

    public static byte[] ParseBase64Aes256Key(string base64Key, string settingName)
    {
        byte[] key;
        try
        {
            key = Convert.FromBase64String(base64Key);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"{settingName} must be valid base64.", ex);
        }

        if (key.Length != 32)
        {
            throw new InvalidOperationException(
                $"{settingName} must decode to exactly 32 bytes for AES-256; got {key.Length}.");
        }

        return key;
    }
}
