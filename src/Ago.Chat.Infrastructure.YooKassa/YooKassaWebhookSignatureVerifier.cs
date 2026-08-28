using System.Security.Cryptography;
using System.Text;
using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Infrastructure.YooKassa;

/// <summary>
/// `13-02`/`adr/0025`: HMAC-SHA256 over <c>{HTTP method}|{URL}|{request body}</c> (this item's own
/// backlog text), keyed by <see cref="YooKassaOptions.WebhookKey"/>. Hex-encoded, constant-time
/// compared (<see cref="CryptographicOperations.FixedTimeEquals"/>) - the same "authenticated value,
/// compared in constant time, never a plain string `==`" discipline `ChannelCredential.MatchesWebhookSecret`
/// already established for MAX's own inbound secret-header check, applied here to a computed digest
/// instead of a stored value.
///
/// <para><b>The hex encoding is this item's own assumption, not a confirmed fact.</b> ЮKassa's own
/// documentation states the header carries an HMAC-SHA256 digest but this environment has no network
/// access to confirm whether that digest is hex or base64 encoded, or whether the header value carries
/// any prefix (contrast `adr/0024`'s own `X-Ago-Signature: t=&lt;unix&gt;,v1=&lt;hex&gt;`, a scheme this
/// codebase designed and therefore controls completely). Hex is this class's default because it is
/// this codebase's own existing convention for an HMAC digest (`adr/0024`) and the more common of the
/// two for this class of API; <see cref="Verify"/> is the one method a real ЮKassa test-mode webhook
/// would immediately prove right or wrong.</para>
/// </summary>
public sealed class YooKassaWebhookSignatureVerifier(YooKassaOptions options) : IYooKassaWebhookSignatureVerifier
{
    public bool Verify(string httpMethod, string requestUrl, string rawBody, string? signatureHeader)
    {
        if (string.IsNullOrEmpty(signatureHeader))
        {
            return false;
        }

        byte[] presented;
        try
        {
            presented = Convert.FromHexString(signatureHeader);
        }
        catch (FormatException)
        {
            return false;
        }

        var canonical = $"{httpMethod}|{requestUrl}|{rawBody}";
        var key = Encoding.UTF8.GetBytes(options.WebhookKey);
        var expected = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonical));

        return CryptographicOperations.FixedTimeEquals(presented, expected);
    }
}
