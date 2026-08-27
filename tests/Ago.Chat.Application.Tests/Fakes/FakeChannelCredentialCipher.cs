using System.Text;
using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>A reversible stand-in, not a real cipher - <see cref="FakeWebhookSecretCipher"/>'s own
/// remarks apply unchanged: Application-layer tests only need "encrypt then decrypt round-trips."</summary>
public sealed class FakeChannelCredentialCipher : IChannelCredentialCipher
{
    public byte[] Encrypt(string token) => Encoding.UTF8.GetBytes(token);

    public string Decrypt(byte[] ciphertext) => Encoding.UTF8.GetString(ciphertext);
}
