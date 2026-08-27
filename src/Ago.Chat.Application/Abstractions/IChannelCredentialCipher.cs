namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `14-02`/`adr/0069`: reversible at-rest protection for <see cref="Domain.ChannelCredential.TokenCiphertext"/>
/// - a shop's own bot token, which AGO must reproduce byte-for-byte on every outbound call to the
/// provider (the <c>Authorization</c> header MAX's Bot API requires). Not a hash, for the identical
/// reason <see cref="IWebhookSecretCipher"/> is not one: this system is a *user* of the secret on an
/// ongoing basis, not merely a *verifier* of one presented back to it. See
/// <see cref="Domain.ChannelCredential"/>'s own remarks for the contrast with
/// <see cref="Domain.ChannelCredential.WebhookSecretHash"/>, which needs no such port at all because it
/// is only ever verified, never reproduced.
///
/// <para>A distinct port from <see cref="IWebhookSecretCipher"/>, and a distinct encryption key
/// (`Channels:CredentialEncryptionKey`, `adr/0069`), even though both implementations share the same
/// AES-256-GCM primitive underneath (<c>Ago.Chat.Infrastructure.Postgres.AesGcmCipher</c>). Two
/// different secrets, owned by two different reasons a key might need to rotate independently of the
/// other - a webhook secret rotates when a tenant re-registers a CRM endpoint; a channel credential key
/// rotates on AGO's own schedule or in response to a leak of *this* key specifically. Sharing one key
/// between them would mean a channel-credential leak investigation has to consider re-encrypting every
/// tenant's webhook secret too, for no reason connected to what actually leaked.</para>
/// </summary>
public interface IChannelCredentialCipher
{
    byte[] Encrypt(string token);

    string Decrypt(byte[] ciphertext);
}
