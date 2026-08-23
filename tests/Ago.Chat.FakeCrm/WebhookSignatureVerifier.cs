using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Ago.Chat.FakeCrm;

/// <summary>
/// Verifies (and, for this project's own proof tests, produces) the <c>X-Ago-Signature</c> header:
/// <c>t=&lt;unix-seconds&gt;,v1=&lt;hex-hmac-sha256&gt;</c> over the string <c>"{t}.{raw body}"</c>,
/// Stripe/GitHub-style. This class does not own that scheme - `6-03`'s webhook-registration ADR does,
/// and was being written concurrently with this harness, so its final text was not readable while
/// this was built. Implemented here against the scheme as this backlog item's own instructions
/// describe it (HMAC-SHA256, the exact header shape above, a short replay window on the timestamp);
/// every place this class had to pick a concrete number or behaviour on its own is called out on the
/// member in question and repeated in this project's README, so a later reconciliation against `6-03`'s
/// actual text has one place to look, not a hunt through the diff.
///
/// A pure function of its inputs - no clock, no secret, no HTTP dependency bound into it - the same
/// "take time as a parameter instead of reading a clock" shape date-and-time.md asks of Domain and
/// Application, even though this project sits outside that layering entirely: it is what makes every
/// rejection reason below individually unit-testable without a running process.
/// </summary>
public static class WebhookSignatureVerifier
{
    private const string TimestampPrefix = "t=";
    private const string SignaturePrefix = "v1=";

    public enum VerificationResult
    {
        Valid,
        HeaderMissing,
        HeaderMalformed,
        TimestampStale,
        SignatureMismatch,
    }

    /// <summary>
    /// Checks a received request against the scheme above. Order matters for what a caller can learn
    /// from the result, not for correctness: a malformed header is reported before a stale timestamp
    /// is even attempted, since there is nothing to check staleness against yet.
    /// </summary>
    public static VerificationResult Verify(
        ReadOnlySpan<byte> rawBody, string? signatureHeader, string signingSecret, DateTimeOffset now, TimeSpan tolerance)
    {
        if (string.IsNullOrEmpty(signatureHeader))
        {
            return VerificationResult.HeaderMissing;
        }

        if (!TryParseHeader(signatureHeader, out var timestamp, out var providedSignatureHex))
        {
            return VerificationResult.HeaderMalformed;
        }

        var signedAt = DateTimeOffset.FromUnixTimeSeconds(timestamp);
        if ((now - signedAt).Duration() > tolerance)
        {
            return VerificationResult.TimestampStale;
        }

        var providedSignature = TryDecodeHex(providedSignatureHex);
        var expectedSignature = ComputeSignature(rawBody, timestamp, signingSecret);

        // Fixed-time comparison: a check that returns faster on the first mismatched byte than on a
        // full match leaks the expected signature one byte at a time to anyone willing to measure -
        // the textbook reason to use CryptographicOperations.FixedTimeEquals here instead of a plain
        // SequenceEqual, even in a disposable test double whose "secret" is a published dev value.
        if (providedSignature is null || !CryptographicOperations.FixedTimeEquals(providedSignature, expectedSignature))
        {
            return VerificationResult.SignatureMismatch;
        }

        return VerificationResult.Valid;
    }

    /// <summary>
    /// Builds a header this class's own <see cref="Verify"/> should accept. Only ever called by this
    /// project's proof tests: a fake CRM has no reason to sign anything of its own in real use, since
    /// it only ever receives deliveries, it never sends any.
    /// </summary>
    public static string Sign(ReadOnlySpan<byte> rawBody, long unixTimestamp, string signingSecret)
    {
        var signatureHex = Convert.ToHexStringLower(ComputeSignature(rawBody, unixTimestamp, signingSecret));
        return string.Create(CultureInfo.InvariantCulture, $"{TimestampPrefix}{unixTimestamp},{SignaturePrefix}{signatureHex}");
    }

    private static byte[] ComputeSignature(ReadOnlySpan<byte> rawBody, long unixTimestamp, string signingSecret)
    {
        var timestampText = unixTimestamp.ToString(CultureInfo.InvariantCulture);
        var signedPayload = new byte[Encoding.ASCII.GetByteCount(timestampText) + 1 + rawBody.Length];
        var written = Encoding.ASCII.GetBytes(timestampText, signedPayload);
        signedPayload[written] = (byte)'.';
        rawBody.CopyTo(signedPayload.AsSpan(written + 1));

        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingSecret), signedPayload);
    }

    private static bool TryParseHeader(string header, out long timestamp, out string signatureHex)
    {
        timestamp = 0;
        signatureHex = "";

        long? parsedTimestamp = null;
        string? parsedSignature = null;
        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith(TimestampPrefix, StringComparison.Ordinal)
                && long.TryParse(part.AsSpan(TimestampPrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                parsedTimestamp = parsed;
            }
            else if (part.StartsWith(SignaturePrefix, StringComparison.Ordinal))
            {
                parsedSignature = part[SignaturePrefix.Length..];
            }
        }

        if (parsedTimestamp is null || string.IsNullOrEmpty(parsedSignature))
        {
            return false;
        }

        timestamp = parsedTimestamp.Value;
        signatureHex = parsedSignature;
        return true;
    }

    private static byte[]? TryDecodeHex(string value)
    {
        try
        {
            return Convert.FromHexString(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
