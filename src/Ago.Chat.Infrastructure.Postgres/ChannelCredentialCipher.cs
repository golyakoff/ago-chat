using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `14-02`/`adr/0069`: AES-256-GCM over a shop's own channel bot token, using the shared
/// <see cref="AesGcmCipher"/> primitive under its own key (`Channels:CredentialEncryptionKey`) - see
/// that class's and <see cref="IChannelCredentialCipher"/>'s own remarks for why this key is distinct
/// from `Webhooks:SecretEncryptionKey` despite the identical algorithm.
/// </summary>
public sealed class ChannelCredentialCipher : IChannelCredentialCipher
{
    private readonly byte[] _key;

    public ChannelCredentialCipher(ChannelCredentialCipherOptions options)
    {
        if (string.IsNullOrEmpty(options.CredentialEncryptionKey))
        {
            throw new InvalidOperationException(
                "Set Channels:CredentialEncryptionKey - a base64-encoded 32-byte AES-256 key (infra-credentials, local-dev.md).");
        }

        _key = AesGcmCipher.ParseBase64Aes256Key(options.CredentialEncryptionKey, "Channels:CredentialEncryptionKey");
    }

    public byte[] Encrypt(string token) => AesGcmCipher.Encrypt(_key, token);

    public string Decrypt(byte[] ciphertext) => AesGcmCipher.Decrypt(_key, ciphertext);
}
