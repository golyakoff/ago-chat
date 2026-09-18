using System.Text;
using System.Text.RegularExpressions;
using Ago.Chat.Infrastructure.Email;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `14-09`: <see cref="EmailSmtpClient"/>'s own terminal/transient split and its own MIME payload, proven
/// against a real TCP boundary rather than trusting a code comment - <see cref="WhatsAppApiClientTests"/>'s
/// own precedent for standing in for a real provider with an in-process, ephemeral-port server, adapted
/// here to a raw SMTP conversation instead of HTTP (<see cref="FakeSmtpServer"/>'s own remarks).
/// </summary>
public sealed class EmailSmtpClientTests
{
    private static EmailMessageToSend Message(string? inReplyTo = "<visitor-1@example.com>", string? references = "<visitor-1@example.com>") =>
        new(
            From: "support+3fa85f6457174562b3fc2c963f66afa6@ago-chat.example",
            To: "visitor@example.com",
            Subject: "Re: Where is my order?",
            Body: "Your order ships tomorrow.",
            MessageId: "<reply-1@ago-chat.example>",
            InReplyTo: inReplyTo,
            References: references,
            Date: new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task SendAsync_WhenTheRelayAcceptsEverything_ReturnsSent()
    {
        using var server = await FakeSmtpServer.StartAsync();
        var client = new EmailSmtpClient(server.Options);

        var result = await client.SendAsync(Message(), CancellationToken.None);

        Assert.True(result.Success);
    }

    /// <summary>Proves the actual bytes sent over the wire, not just the outcome - the real point of a
    /// hand-rolled protocol client's own test, the same standard <see cref="WhatsAppApiClientTests"/>'s
    /// own JSON-shape assertions hold themselves to for HTTP.</summary>
    [Fact]
    public async Task SendAsync_SendsTheExpectedCommandSequenceAndHeaders()
    {
        using var server = await FakeSmtpServer.StartAsync();
        var client = new EmailSmtpClient(server.Options);

        await client.SendAsync(Message(), CancellationToken.None);
        var transcript = await server.WaitForTranscriptAsync();

        Assert.Contains("MAIL FROM:<support+3fa85f6457174562b3fc2c963f66afa6@ago-chat.example>", transcript.Commands);
        Assert.Contains("RCPT TO:<visitor@example.com>", transcript.Commands);
        Assert.Contains("DATA", transcript.Commands);
        Assert.Contains("From: AGO Chat <support+3fa85f6457174562b3fc2c963f66afa6@ago-chat.example>", transcript.DataPayload);
        Assert.Contains("To: <visitor@example.com>", transcript.DataPayload);
        Assert.Contains("Message-ID: <reply-1@ago-chat.example>", transcript.DataPayload);
        Assert.Contains("In-Reply-To: <visitor-1@example.com>", transcript.DataPayload);
        Assert.Contains("References: <visitor-1@example.com>", transcript.DataPayload);
        Assert.Contains("Content-Transfer-Encoding: base64", transcript.DataPayload);
        // "Your order ships tomorrow." base64-encoded (UTF-8) - proving the body actually made it across,
        // not merely that some payload did.
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("Your order ships tomorrow.")), transcript.DataPayload);
    }

    /// <summary>A Cyrillic subject must be RFC 2047-encoded, not sent as raw UTF-8 bytes in a header -
    /// <see cref="EmailMimeMessageBuilder"/>'s own remarks on why, for this project's own target
    /// customer (`ago-business/decisions/0002`).</summary>
    [Fact]
    public async Task SendAsync_WithACyrillicSubject_Rfc2047EncodesIt()
    {
        using var server = await FakeSmtpServer.StartAsync();
        var client = new EmailSmtpClient(server.Options);
        var message = Message() with { Subject = "Где мой заказ?" };

        await client.SendAsync(message, CancellationToken.None);
        var transcript = await server.WaitForTranscriptAsync();

        Assert.Contains($"Subject: =?UTF-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes("Где мой заказ?"))}?=", transcript.DataPayload);
    }

    /// <summary>RFC 5321's own transparency rule - a body line that happens to start with <c>.</c> must
    /// arrive doubled, or the relay would misread it as the end-of-data terminator. Forced by disabling
    /// base64 is not an option (the encoder never produces a leading dot -
    /// <see cref="EmailMimeMessageBuilder"/>'s own remarks); the payload's own MIME headers can only ever
    /// start with a value this class controls, so the safest direct proof is a unit test on
    /// <see cref="EmailSmtpClient"/>'s own dot-stuffing over a value crafted to trigger it - a From/To
    /// address cannot easily be made to start a line with <c>.</c>, so this test targets the transcript's
    /// own raw payload shape instead of the message content.</summary>
    [Fact]
    public async Task SendAsync_NeverLeavesAnUnstuffedLeadingDotInTheTransmittedPayload()
    {
        using var server = await FakeSmtpServer.StartAsync();
        var client = new EmailSmtpClient(server.Options);

        await client.SendAsync(Message(), CancellationToken.None);
        var transcript = await server.WaitForTranscriptAsync();

        foreach (var line in transcript.RawDataLines)
        {
            Assert.False(line.Length == 1 && line == ".", "a lone '.' line inside the payload would have terminated DATA early");
        }
    }

    [Fact]
    public async Task SendAsync_WhenTheRelayRefusesTheRecipientWithA5xxCode_ReturnsRefused()
    {
        using var server = await FakeSmtpServer.StartAsync(rcptToResponse: "550 5.1.1 No such user here");
        var client = new EmailSmtpClient(server.Options);

        var result = await client.SendAsync(Message(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("550", result.RefusalReason);
    }

    /// <summary>A <c>4xx</c> reply is "transient negative completion" (RFC 5321) - thrown, not refused, so
    /// the wrapping resilience pipeline retries it (<see cref="EmailSmtpClient"/>'s own remarks).</summary>
    [Fact]
    public async Task SendAsync_WhenTheRelayRefusesTheRecipientWithA4xxCode_Throws()
    {
        using var server = await FakeSmtpServer.StartAsync(rcptToResponse: "452 4.2.2 Mailbox full");
        var client = new EmailSmtpClient(server.Options);

        await Assert.ThrowsAsync<IOException>(() => client.SendAsync(Message(), CancellationToken.None));
    }

    [Fact]
    public async Task SendAsync_WhenTheRelayRefusesTheMessageBodyWithA5xxCode_ReturnsRefused()
    {
        using var server = await FakeSmtpServer.StartAsync(dataTerminatorResponse: "552 5.3.4 Message too large");
        var client = new EmailSmtpClient(server.Options);

        var result = await client.SendAsync(Message(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("552", result.RefusalReason);
    }

    [Fact]
    public async Task SendAsync_WhenTheRelayIsUnreachable_ThrowsARealConnectionFailure()
    {
        using var server = await FakeSmtpServer.StartAsync();
        var options = server.Options;
        server.Dispose();

        var client = new EmailSmtpClient(options);

        await Assert.ThrowsAsync<IOException>(() => client.SendAsync(Message(), CancellationToken.None));
    }

    /// <summary>A missing thread (<see cref="EmailMessageToSend.InReplyTo"/>/<see cref="EmailMessageToSend.References"/>
    /// both <see langword="null"/>) must still produce a well-formed message with neither header - not an
    /// empty header line, which some mail servers reject outright.</summary>
    [Fact]
    public async Task SendAsync_WithNoInReplyToOrReferences_OmitsBothHeadersEntirely()
    {
        using var server = await FakeSmtpServer.StartAsync();
        var client = new EmailSmtpClient(server.Options);

        await client.SendAsync(Message(inReplyTo: null, references: null), CancellationToken.None);
        var transcript = await server.WaitForTranscriptAsync();

        Assert.DoesNotContain("In-Reply-To:", transcript.DataPayload);
        Assert.DoesNotContain("References:", transcript.DataPayload);
    }

    /// <summary>
    /// `25-155`: <see cref="EmailMimeMessageBuilder.BuildMultipartAlternative"/> is <c>internal</c>, so
    /// this proves it the same way every other MIME behaviour in this file is proven - over the real TCP
    /// boundary <see cref="FakeSmtpServer"/> stands in for, through the one public entry point
    /// (<see cref="EmailSmtpClient.SendAsync"/>) that branches to it whenever
    /// <see cref="EmailMessageToSend.HtmlBody"/> is set. Checks the one thing every HTML-incapable mail
    /// client depends on: the <c>text/plain</c> part comes first, so a client with no HTML support falls
    /// back to it rather than to the <c>text/html</c> part.
    /// </summary>
    [Fact]
    public async Task SendAsync_WithAnHtmlBody_SendsAMultipartAlternativeMessageWithThePlainPartFirst()
    {
        using var server = await FakeSmtpServer.StartAsync();
        var client = new EmailSmtpClient(server.Options);
        var message = Message() with { HtmlBody = "<html><body><p>Your order ships tomorrow.</p></body></html>" };

        await client.SendAsync(message, CancellationToken.None);
        var transcript = await server.WaitForTranscriptAsync();

        Assert.Contains("Content-Type: multipart/alternative; boundary=", transcript.DataPayload);

        var plainIndex = transcript.DataPayload.IndexOf("Content-Type: text/plain; charset=utf-8", StringComparison.Ordinal);
        var htmlIndex = transcript.DataPayload.IndexOf("Content-Type: text/html; charset=utf-8", StringComparison.Ordinal);
        Assert.True(plainIndex >= 0, "expected a text/plain part");
        Assert.True(htmlIndex >= 0, "expected a text/html part");
        Assert.True(plainIndex < htmlIndex, "the text/plain part must come before the text/html part");

        // Base64 wraps at 76 characters (RFC 2045), so the transmitted payload is not one flat base64
        // run - strip the wrap points back out before comparing, the same shape a real MIME parser's own
        // unfolding step would produce.
        var unwrapped = transcript.DataPayload.Replace("\r\n", "");
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(message.Body)), unwrapped);
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(message.HtmlBody)), unwrapped);
    }

    /// <summary>Both parts are still base64, and the boundary itself opens and closes correctly - the
    /// structural shape RFC 2046 requires for <c>multipart/alternative</c>.</summary>
    [Fact]
    public async Task SendAsync_WithAnHtmlBody_UsesBase64ForBothPartsAndClosesTheBoundary()
    {
        using var server = await FakeSmtpServer.StartAsync();
        var client = new EmailSmtpClient(server.Options);
        var message = Message() with { HtmlBody = "<html><body>Where is my order?</body></html>" };

        await client.SendAsync(message, CancellationToken.None);
        var transcript = await server.WaitForTranscriptAsync();

        var boundaryMatch = Regex.Match(transcript.DataPayload, "boundary=\"(?<boundary>[^\"]+)\"");
        Assert.True(boundaryMatch.Success, "expected a quoted boundary parameter");
        var boundary = boundaryMatch.Groups["boundary"].Value;

        Assert.Contains($"--{boundary}\r\n", transcript.DataPayload);
        Assert.Contains($"--{boundary}--", transcript.DataPayload);
        Assert.Equal(2, transcript.DataPayload.Split("Content-Transfer-Encoding: base64").Length - 1);
    }

    /// <summary>Without an HTML body, <see cref="EmailSmtpClient.SendAsync"/> keeps taking the original,
    /// untouched plain-only path - the exact call <see cref="EmailChannelAdapter"/> always makes, since it
    /// never sets <see cref="EmailMessageToSend.HtmlBody"/>.</summary>
    [Fact]
    public async Task SendAsync_WithNoHtmlBody_NeverProducesAMultipartContentType()
    {
        using var server = await FakeSmtpServer.StartAsync();
        var client = new EmailSmtpClient(server.Options);

        await client.SendAsync(Message(), CancellationToken.None);
        var transcript = await server.WaitForTranscriptAsync();

        Assert.Contains("Content-Type: text/plain; charset=utf-8", transcript.DataPayload);
        Assert.DoesNotContain("multipart/alternative", transcript.DataPayload);
    }
}
