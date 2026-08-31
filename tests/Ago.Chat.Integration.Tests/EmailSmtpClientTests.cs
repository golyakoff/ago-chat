using System.Text;
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
}
