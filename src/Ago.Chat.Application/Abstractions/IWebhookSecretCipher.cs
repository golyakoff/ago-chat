namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// Reversible at-rest protection for a webhook endpoint's plaintext secret (`adr/00XX`). Not a hash:
/// this system is the *signer* of every future delivery (`adr/0013`'s dispatcher, `6-05`), not merely
/// a *verifier* the way a login form is against a stored password hash - it must reproduce the exact
/// secret bytes to compute an HMAC-SHA256 signature on each delivery, which a one-way hash cannot
/// support. See <see cref="Domain.WebhookEndpoint"/>'s own remarks and the ADR for the full reasoning.
///
/// This item's own scope has no caller for <see cref="Decrypt"/> - nothing it ships ever reads a
/// secret back (list/delivery-history responses never include one). It is declared now, alongside
/// <see cref="Encrypt"/>, because the two are one reversible primitive, not two separate features -
/// withholding `Decrypt` would leave a cipher port that cannot, even in principle, be used for the
/// reason it exists. `6-05`'s dispatcher is its first real caller.
/// </summary>
public interface IWebhookSecretCipher
{
    byte[] Encrypt(string secret);

    string Decrypt(byte[] ciphertext);
}
