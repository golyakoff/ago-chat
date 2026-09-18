using System.Text;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Infrastructure.Email;
using Ago.Chat.Worker;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `25-155`: <see cref="NotificationMailSender"/>'s own real-send proof that it can produce and transmit a
/// genuine <c>multipart/alternative</c> email - the identical <see cref="FakeSmtpServer"/> real-TCP
/// mechanism <see cref="EmailSmtpClientTests"/>/<see cref="EmailChannelAdapterTests"/> already establish,
/// reused here rather than a second fake (this item's own instruction: prove the multipart path "using
/// whichever real-send test mechanism you found already exists in this codebase").
///
/// <para>Drives <see cref="InactivityWarningMailTemplate"/> - one of the three templates `25-155` rewires
/// through <c>Ago.Chat.Application.Emailing.EmailHtmlShell</c> - end to end through
/// <see cref="NotificationMailSender.SendAsync"/>, proving this item's own Done-when ("all three render
/// through the one shared HTML shell, proven by a real send for at least one of them") against the actual
/// send path rather than against the shell's own output in isolation.</para>
/// </summary>
public sealed class NotificationMailSenderTests
{
    [Fact]
    public async Task SendAsync_WithATemplateBuiltThroughTheSharedShell_SendsARealMultipartAlternativeEmail()
    {
        using var server = await FakeSmtpServer.StartAsync();
        var sender = new NotificationMailSender(
            new EmailSmtpClient(server.Options), Options.Create(server.Options), new FixedClock(),
            NullLogger<NotificationMailSender>.Instance);

        var deletionDate = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
        var (subject, body, htmlBody) = InactivityWarningMailTemplate.Build(
            "Acme Support", daysRemaining: 14, deletionDate, loginUrl: "https://office.example.test/login");

        await sender.SendAsync(
            new NotificationMailMessage("owner@example.com", subject, body, htmlBody), CancellationToken.None);
        var transcript = await server.WaitForTranscriptAsync();

        Assert.Contains("RCPT TO:<owner@example.com>", transcript.Commands);
        Assert.Contains("Content-Type: multipart/alternative; boundary=", transcript.DataPayload);

        var plainIndex = transcript.DataPayload.IndexOf("Content-Type: text/plain; charset=utf-8", StringComparison.Ordinal);
        var htmlIndex = transcript.DataPayload.IndexOf("Content-Type: text/html; charset=utf-8", StringComparison.Ordinal);
        Assert.True(plainIndex >= 0 && htmlIndex >= 0 && plainIndex < htmlIndex,
            "the text/plain part must be present and come before the text/html part");

        // Both parts actually made it across the wire, base64-encoded exactly like the existing
        // plain-only path already is. Base64 wraps at 76 characters (RFC 2045), so the wire payload is
        // not one flat base64 run - strip the wrap points back out before comparing.
        var unwrapped = transcript.DataPayload.Replace("\r\n", "");
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(body)), unwrapped);
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(htmlBody)), unwrapped);

        // The HTML rendering carries the shared shell's own structure and the same site name the
        // plain-text body carries - the same content, a different rendering, not a different message
        // (`docs/backlog/25-155-*.md`'s own "same wording, new rendering").
        Assert.Contains("Acme Support", htmlBody);
        Assert.Contains("<!DOCTYPE html", htmlBody);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
    }
}
