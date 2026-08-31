using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace Ago.Chat.Infrastructure.Email;

/// <summary>
/// `14-09`: the one class in this codebase that speaks SMTP - deliberately thin, no retry, no timeout, no
/// circuit breaker (`ChannelKind.Email`'s adapter, wrapped in
/// <c>Ago.Chat.Module.Channels.ResilientInboundChannelAdapter</c>, is where all four of those live for
/// <see cref="SendAsync"/>), the identical division <c>MaxApiClient</c>/<c>VkApiClient</c>/
/// <c>WhatsAppApiClient</c> already establish for their own outbound HTTP calls - here for SMTP instead.
///
/// <para><b>Why this is hand-rolled over raw sockets, not a NuGet package (MailKit, the de facto standard
/// .NET SMTP library).</b> CLAUDE.md: "Do not add a NuGet package without saying what it replaces and why
/// hand-rolling is worse." Every other channel adapter in this codebase already hand-rolls its own
/// provider's protocol over a BCL primitive - <c>HttpClient</c> for MAX/VK/WhatsApp/Avito's own JSON-over-
/// HTTPS APIs - rather than reaching for a provider SDK; SMTP is a comparably simple, well-specified text
/// protocol (RFC 5321), and this deployment's one real relay (`10-05`'s self-hosted Postfix) needs neither
/// authentication nor TLS, which is most of what a full-featured SMTP library exists to handle. Hand-
/// rolling the actual command sequence this item needs (<c>EHLO</c>/<c>MAIL FROM</c>/<c>RCPT TO</c>/
/// <c>DATA</c>/<c>QUIT</c>, reply-code parsing, dot-stuffing) is a similarly-sized, similarly-scoped piece
/// of code to <see cref="WhatsAppApiClient"/>, not a reimplementation of a whole library - the same
/// judgement call this project already made four times over for HTTP.</para>
///
/// <para><b>What this class deliberately does not implement, and why that is safe for this deployment's
/// one real relay.</b> No <c>STARTTLS</c>, no <c>AUTH</c>, no <c>8BITMIME</c>/<c>SMTPUTF8</c> extension
/// negotiation. `10-05`'s own report is explicit that its self-hosted relay is reached over a cluster-
/// internal hop (<c>10.42.0.1:25</c>) with "no SASL, no TLS needed" - the one real target this class talks
/// to today needs none of these. A future deployment that points <see cref="EmailBotApiOptions.SmtpHost"/>
/// at a relay that does require them (a different network path, a different provider) would need this
/// class extended first - named here as a real, load-bearing assumption rather than left to be discovered
/// as a mysterious connection failure.</para>
///
/// <para><b>The terminal/transient split, mapped onto SMTP's own two-digit reply-code families.</b> A
/// <c>5xx</c> reply to <c>RCPT TO</c> or to the final <c>DATA</c> terminator (RFC 5321's own "permanent
/// negative completion") is a real, expected outcome - an unknown recipient, a relay policy refusal - and
/// comes back as <see cref="EmailSendResult.Refused"/>; retrying it would never help, the identical
/// reasoning every other channel's own terminal-refusal code table applies to its own vendor-specific
/// codes. A <c>4xx</c> reply ("transient negative completion" - a full mailbox, a temporarily unreachable
/// downstream hop) is thrown, because throwing is what the wrapping resilience pipeline acts on
/// (<see cref="Application.Abstractions.IInboundChannelAdapter.SendAsync"/>'s own contract). Anything at
/// the connection/greeting/<c>EHLO</c>/<c>MAIL FROM</c> stage that is not <c>2xx</c> is also thrown - this
/// system's own sender address and this relay's own greeting are not expected to ever be refused in
/// ordinary operation, so a refusal there is treated as an infrastructure fault rather than a per-message
/// outcome, the same "should not happen, thrown rather than silently accepted" split
/// <see cref="Domain.ChannelCredential"/>'s own remarks describe for a missing <c>ProviderAccountId</c>.</para>
///
/// <para><b>No per-call idempotency key - the identical, already-named gap MAX's, Telegram's and
/// WhatsApp's own outbound clients each carry.</b> SMTP has nothing resembling VK's own <c>random_id</c>;
/// a resilience-pipeline retry after a transient <c>4xx</c> could, in principle, cause the relay to accept
/// the identical message twice, producing a visible duplicate in the visitor's inbox. Named plainly rather
/// than silently accepted, the same discipline every precedent's own remarks already apply to
/// themselves.</para>
/// </summary>
public sealed class EmailSmtpClient(EmailBotApiOptions options)
{
    private static readonly Encoding Ascii = Encoding.ASCII;

    public async Task<EmailSendResult> SendAsync(EmailMessageToSend message, CancellationToken cancellationToken)
    {
        using var tcpClient = new TcpClient();
        try
        {
            await tcpClient.ConnectAsync(options.SmtpHost, options.SmtpPort, cancellationToken);
        }
        catch (SocketException ex)
        {
            throw new IOException(
                $"Could not connect to the SMTP relay at {options.SmtpHost}:{options.SmtpPort}.", ex);
        }

        await using var stream = tcpClient.GetStream();
        using var reader = new StreamReader(stream, Ascii, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        await using var writer = new StreamWriter(stream, Ascii, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };

        EnsureSuccess(await ReadReplyAsync(reader, cancellationToken), "the relay's own greeting", expectedCode: 220);

        await SendLineAsync(writer, $"EHLO {SafeDomain()}", cancellationToken);
        EnsureSuccess(await ReadReplyAsync(reader, cancellationToken), "EHLO");

        await SendLineAsync(writer, $"MAIL FROM:<{message.From}>", cancellationToken);
        EnsureSuccess(await ReadReplyAsync(reader, cancellationToken), "MAIL FROM");

        await SendLineAsync(writer, $"RCPT TO:<{message.To}>", cancellationToken);
        var rcptReply = await ReadReplyAsync(reader, cancellationToken);
        if (IsPermanentRefusal(rcptReply.Code))
        {
            await TryQuitAsync(writer, reader, cancellationToken);
            return EmailSendResult.Refused($"The SMTP relay refused the recipient (code {rcptReply.Code}): {rcptReply.Text}");
        }

        EnsureSuccess(rcptReply, "RCPT TO");

        await SendLineAsync(writer, "DATA", cancellationToken);
        EnsureSuccess(await ReadReplyAsync(reader, cancellationToken), "DATA", expectedCode: 354);

        var payload = DotStuff(EmailMimeMessageBuilder.Build(message));
        await writer.WriteAsync(payload.AsMemory(), cancellationToken);
        await writer.WriteAsync("\r\n.\r\n".AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);

        var finalReply = await ReadReplyAsync(reader, cancellationToken);
        if (IsPermanentRefusal(finalReply.Code))
        {
            await TryQuitAsync(writer, reader, cancellationToken);
            return EmailSendResult.Refused($"The SMTP relay refused the message (code {finalReply.Code}): {finalReply.Text}");
        }

        EnsureSuccess(finalReply, "the DATA terminator");

        await TryQuitAsync(writer, reader, cancellationToken);
        return EmailSendResult.Sent(message.MessageId);
    }

    /// <summary>The EHLO hostname argument is meant to identify *this* system to the relay - a plain,
    /// always-ASCII fallback is used if <see cref="EmailBotApiOptions.Domain"/> somehow is not (it should
    /// always be an ASCII domain name in practice; this is a defensive floor, not an expected path).</summary>
    private string SafeDomain() => options.Domain is { Length: > 0 } domain ? domain : "localhost";

    /// <summary><c>4xx</c> is "transient negative completion" (RFC 5321) - thrown, not refused, so the
    /// wrapping resilience pipeline retries it (<c>ChannelResiliencePipelines.IsRetryWorthy</c>'s own
    /// "everything reaching here is already a transient fault" contract).</summary>
    private static bool IsPermanentRefusal(int code) => code is >= 500 and < 600;

    private static void EnsureSuccess(SmtpReply reply, string step, int expectedCode = 250)
    {
        if (reply.Code == expectedCode || (expectedCode == 250 && reply.Code is >= 200 and < 300))
        {
            return;
        }

        throw new IOException($"The SMTP relay refused {step} (code {reply.Code}): {reply.Text}");
    }

    private static async Task TryQuitAsync(StreamWriter writer, StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            await SendLineAsync(writer, "QUIT", cancellationToken);
            await ReadReplyAsync(reader, cancellationToken);
        }
        catch (IOException)
        {
            // Best-effort only - the send outcome (Refused, above) is already decided; a relay that closes
            // the connection before answering QUIT has told this client everything it needs to know.
        }
    }

    private static Task SendLineAsync(StreamWriter writer, string line, CancellationToken cancellationToken) =>
        writer.WriteLineAsync(line.AsMemory(), cancellationToken);

    private static async Task<SmtpReply> ReadReplyAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var textLines = new List<string>();
        int code;

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is not { Length: >= 4 } || !int.TryParse(line[..3], NumberStyles.None, CultureInfo.InvariantCulture, out code))
            {
                throw new IOException("The SMTP relay closed the connection or sent a malformed reply.");
            }

            textLines.Add(line[4..]);

            if (line[3] != '-')
            {
                break;
            }
        }

        return new SmtpReply(code, string.Join(" ", textLines));
    }

    /// <summary>RFC 5321's own transparency rule: a line beginning with <c>.</c> anywhere in the
    /// <c>DATA</c> payload must have a second <c>.</c> prepended, or the relay would read it as the
    /// end-of-data terminator. Applied to the whole payload, not only the body -
    /// <see cref="EmailMimeMessageBuilder"/>'s own remarks explain why the base64-encoded body in practice
    /// never triggers this, but a header value in principle could, and a correct SMTP client does not get
    /// to assume its own content is always safe.</summary>
    private static string DotStuff(string payload)
    {
        var lines = payload.Split("\r\n");
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith('.'))
            {
                lines[i] = "." + lines[i];
            }
        }

        return string.Join("\r\n", lines);
    }

    private readonly record struct SmtpReply(int Code, string Text);
}

/// <summary>The terminal result of one <see cref="EmailSmtpClient.SendAsync"/> call - the SMTP-shaped
/// analogue of <c>WhatsAppSendResult</c>/<c>VkSendResult</c>.</summary>
public sealed record EmailSendResult(bool Success, string? ProviderMessageId, string? RefusalReason)
{
    public static EmailSendResult Sent(string? providerMessageId) => new(true, providerMessageId, null);

    public static EmailSendResult Refused(string reason) => new(false, null, reason);
}
