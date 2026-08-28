namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `13-02`/`adr/0025`: verifies ЮKassa's own `Webhook-Signature` header - HMAC-SHA256 over
/// <c>{HTTP method}|{URL}|{request body}</c>, keyed by the fixed application `webhook_key`
/// (<c>Billing:YooKassa:WebhookKey</c>). A port here, not a static helper, for the same reason
/// <c>IWebhookSecretCipher</c> is a port rather than a static AES call: the key is a real secret that
/// must come from DI-bound options, never a compile-time constant, and Application must stay ignorant
/// of which cipher/HMAC primitive or options-binding mechanism supplies it (clean-architecture.md's
/// dependency rule - Application depends on the abstraction, `Ago.Chat.Infrastructure.YooKassa` on the
/// concrete `HMACSHA256`).
/// </summary>
public interface IYooKassaWebhookSignatureVerifier
{
    /// <summary><paramref name="rawBody"/> must be the exact bytes ЮKassa signed - re-serializing a
    /// parsed object would almost certainly produce a different byte sequence (key order, whitespace,
    /// number formatting) and make every signature fail to verify, which is why every caller of this
    /// method must read the raw request body as text before parsing it as JSON, never after.</summary>
    bool Verify(string httpMethod, string requestUrl, string rawBody, string? signatureHeader);
}
