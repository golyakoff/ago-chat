using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Ago.Chat.Webhooks;

/// <summary>
/// `6-05`: the signing half of `adr/0024`'s scheme - HMAC-SHA256 over
/// <c>"{unix-timestamp}.{raw request body}"</c>, header shape
/// <c>X-Ago-Signature: t=&lt;unix-seconds&gt;,v1=&lt;hex&gt;</c>. This project never references
/// <c>Ago.Chat.FakeCrm.WebhookSignatureVerifier</c> (a `tests/` project, and the *verifying* half of
/// the same scheme built for `6-04`'s own harness) - a production host does not depend on a test
/// project, so the two independently implement the identical byte-for-byte construction the ADR
/// specifies, which is exactly what makes a real request from this class pass that harness's real
/// verification in the integration tests, rather than the two merely agreeing by shared code.
///
/// A pure function of its inputs, the same "no clock, no HTTP dependency bound in" shape
/// `WebhookSignatureVerifier` itself uses and for the same reason: independently unit-testable without
/// a running process or a real secret.
/// </summary>
public static class WebhookSignature
{
    public static string BuildHeader(ReadOnlySpan<byte> rawBody, long unixTimestamp, string signingSecret)
    {
        var timestampText = unixTimestamp.ToString(CultureInfo.InvariantCulture);
        var signedPayload = new byte[Encoding.ASCII.GetByteCount(timestampText) + 1 + rawBody.Length];
        var written = Encoding.ASCII.GetBytes(timestampText, signedPayload);
        signedPayload[written] = (byte)'.';
        rawBody.CopyTo(signedPayload.AsSpan(written + 1));

        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingSecret), signedPayload);
        return string.Create(CultureInfo.InvariantCulture, $"t={timestampText},v1={Convert.ToHexStringLower(signature)}");
    }
}
