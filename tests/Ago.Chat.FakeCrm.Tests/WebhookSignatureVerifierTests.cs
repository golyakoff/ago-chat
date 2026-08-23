namespace Ago.Chat.FakeCrm.Tests;

/// <summary>
/// Pure unit tests of the signature scheme's own math - no process, no socket, deterministic and
/// millisecond-fast. <see cref="FakeCrmPersonalityTests"/> covers the same two rejection reasons again
/// over a real running instance, proving the HTTP wiring, not just the algorithm.
/// </summary>
public sealed class WebhookSignatureVerifierTests
{
    private const string Secret = "unit-test-secret";
    private static readonly byte[] Body = "{\"event\":\"message.created\"}"u8.ToArray();

    [Fact]
    public void Verify_ValidSignature_ReturnsValid()
    {
        var now = DateTimeOffset.UtcNow;
        var signature = WebhookSignatureVerifier.Sign(Body, now.ToUnixTimeSeconds(), Secret);

        var result = WebhookSignatureVerifier.Verify(Body, signature, Secret, now, TimeSpan.FromMinutes(5));

        Assert.Equal(WebhookSignatureVerifier.VerificationResult.Valid, result);
    }

    [Fact]
    public void Verify_TamperedBody_ReturnsSignatureMismatch()
    {
        var now = DateTimeOffset.UtcNow;
        var signature = WebhookSignatureVerifier.Sign(Body, now.ToUnixTimeSeconds(), Secret);
        var tamperedBody = "{\"event\":\"message.deleted\"}"u8.ToArray();

        var result = WebhookSignatureVerifier.Verify(tamperedBody, signature, Secret, now, TimeSpan.FromMinutes(5));

        Assert.Equal(WebhookSignatureVerifier.VerificationResult.SignatureMismatch, result);
    }

    [Fact]
    public void Verify_WrongSecret_ReturnsSignatureMismatch()
    {
        var now = DateTimeOffset.UtcNow;
        var signature = WebhookSignatureVerifier.Sign(Body, now.ToUnixTimeSeconds(), "a-different-secret");

        var result = WebhookSignatureVerifier.Verify(Body, signature, Secret, now, TimeSpan.FromMinutes(5));

        Assert.Equal(WebhookSignatureVerifier.VerificationResult.SignatureMismatch, result);
    }

    [Fact]
    public void Verify_StaleTimestamp_ReturnsTimestampStale()
    {
        var signedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var signature = WebhookSignatureVerifier.Sign(Body, signedAt.ToUnixTimeSeconds(), Secret);

        var result = WebhookSignatureVerifier.Verify(Body, signature, Secret, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));

        Assert.Equal(WebhookSignatureVerifier.VerificationResult.TimestampStale, result);
    }

    [Fact]
    public void Verify_FutureTimestampBeyondTolerance_ReturnsTimestampStale()
    {
        // A clock-skewed or replayed-into-the-future request is rejected the same way a stale one is
        // - the check is "|now - t| > tolerance", not "t is in the past."
        var signedAt = DateTimeOffset.UtcNow.AddMinutes(10);
        var signature = WebhookSignatureVerifier.Sign(Body, signedAt.ToUnixTimeSeconds(), Secret);

        var result = WebhookSignatureVerifier.Verify(Body, signature, Secret, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));

        Assert.Equal(WebhookSignatureVerifier.VerificationResult.TimestampStale, result);
    }

    [Fact]
    public void Verify_MissingHeader_ReturnsHeaderMissing()
    {
        var result = WebhookSignatureVerifier.Verify(Body, null, Secret, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));

        Assert.Equal(WebhookSignatureVerifier.VerificationResult.HeaderMissing, result);
    }

    [Theory]
    [InlineData("not-the-right-shape")]
    [InlineData("t=not-a-number,v1=abcd")]
    [InlineData("v1=abcd")]
    [InlineData("t=1234567890")]
    public void Verify_MalformedHeader_ReturnsHeaderMalformed(string header)
    {
        var result = WebhookSignatureVerifier.Verify(Body, header, Secret, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));

        Assert.Equal(WebhookSignatureVerifier.VerificationResult.HeaderMalformed, result);
    }
}
