using Ago.Chat.Infrastructure.Postgres;

namespace Ago.Chat.Integration.Tests;

/// <summary>No fixture needed - `AesGcm` is pure BCL cryptography, no external resource, the same
/// "infra-adjacent, no Testcontainer required" shape <see cref="DrainReadinessTests"/> already uses.
/// Proves the property `RegisterWebhookEndpointHandlerTests` cannot: with the real cipher (not
/// `FakeWebhookSecretCipher`'s UTF-8 passthrough), the stored ciphertext is not the plaintext, and it
/// really does round-trip.</summary>
public sealed class WebhookSecretCipherTests
{
    private static readonly WebhookSecretCipherOptions Options = new()
    {
        // A fixed, test-only key - never the value any real deployment uses (WebhookSecretCipherOptions'
        // own remarks), the same "throwaway value, safe to commit" shape appsettings.Development.json
        // already uses for its own dev-only key.
        SecretEncryptionKey = "Vg1G2KjonUB1uH8trETJzr30EPoeqt0YRGzYibDKy1o=",
    };

    [Fact]
    public void Encrypt_ThenDecrypt_RoundTripsTheOriginalSecret()
    {
        var cipher = new WebhookSecretCipher(Options);

        var ciphertext = cipher.Encrypt("whsec_super-secret-value");
        var plaintext = cipher.Decrypt(ciphertext);

        Assert.Equal("whsec_super-secret-value", plaintext);
    }

    [Fact]
    public void Encrypt_NeverProducesThePlaintextBytesVerbatim()
    {
        var cipher = new WebhookSecretCipher(Options);
        var secret = "whsec_super-secret-value";

        var ciphertext = cipher.Encrypt(secret);

        Assert.False(ContainsSubsequence(ciphertext, System.Text.Encoding.UTF8.GetBytes(secret)));
    }

    private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public void Encrypt_CalledTwiceForTheSameSecret_ProducesDifferentCiphertext()
    {
        // A fresh random nonce every call (WebhookSecretCipher's own remarks) - the same plaintext
        // must never produce the same ciphertext bytes twice, or an observer could tell two endpoints
        // share a secret without ever decrypting either.
        var cipher = new WebhookSecretCipher(Options);

        var first = cipher.Encrypt("whsec_same-secret");
        var second = cipher.Encrypt("whsec_same-secret");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Decrypt_WhenTheCiphertextWasTampered_ThrowsRatherThanReturningGarbage()
    {
        var cipher = new WebhookSecretCipher(Options);
        var ciphertext = cipher.Encrypt("whsec_super-secret-value");
        ciphertext[^1] ^= 0xFF; // flip the last byte of the authenticated ciphertext

        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => cipher.Decrypt(ciphertext));
    }

    [Fact]
    public void Constructor_WhenTheKeyIsMissing_ThrowsRatherThanSilentlyFallingBack()
    {
        // Deliberately no random-per-process fallback (WebhookSecretCipherOptions' own remarks) -
        // unlike the JWT signing key, a lost encryption key would make every already-registered
        // webhook secret permanently unrecoverable.
        Assert.Throws<InvalidOperationException>(() => new WebhookSecretCipher(new WebhookSecretCipherOptions()));
    }
}
