using System.Text;
using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>A reversible stand-in, not a real cipher - Application-layer tests only need "encrypt then
/// decrypt round-trips," never AES-GCM's actual security properties (those are
/// `WebhookSecretCipherTests`' job, against the real `Ago.Chat.Infrastructure.Postgres` implementation).
/// Plain UTF-8 bytes, so a test asserting "the stored ciphertext is not the plaintext" would need a
/// real cipher fake instead - none of this item's handler tests need that distinction.</summary>
public sealed class FakeWebhookSecretCipher : IWebhookSecretCipher
{
    public byte[] Encrypt(string secret) => Encoding.UTF8.GetBytes(secret);

    public string Decrypt(byte[] ciphertext) => Encoding.UTF8.GetString(ciphertext);
}
