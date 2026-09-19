using System.Globalization;
using System.Text;

namespace Ago.Chat.Infrastructure.Email;

/// <summary>
/// `14-09`: builds the raw RFC 5322 message <see cref="EmailSmtpClient"/> hands to the relay's own
/// <c>DATA</c> command - the MIME half of this channel's own "no rich HTML rendering... plain text"
/// scope cut (this item's own backlog note), kept deliberately simple: one <c>text/plain</c> part, no
/// multipart, no attachments.
///
/// <para><b>Content-Transfer-Encoding: base64, not 8bit or quoted-printable.</b> A visitor's or
/// operator's message body is arbitrary UTF-8 - real Cyrillic text is the expected common case, not an
/// edge case, for this project's own target customer (`ago-business/decisions/0002`). <c>8bit</c> would
/// only be safe if every relay in the path negotiated the SMTP <c>8BITMIME</c> extension, which this
/// hand-rolled client does not check for (`EmailSmtpClient`'s own remarks on what it deliberately does
/// not implement); <c>quoted-printable</c> is byte-safe but needs its own line-length and escaping rules.
/// <c>base64</c> is byte-safe with the simplest possible encoding rule (wrap every 76 characters,
/// RFC 2045's own limit) and, as a useful side effect, never produces a line starting with <c>.</c> - the
/// one character <see cref="EmailSmtpClient"/>'s own dot-stuffing step exists to guard against - so the
/// body half of a message can never accidentally trigger it (headers still can, in principle, which is
/// why dot-stuffing is applied to the whole payload rather than only the body).</para>
///
/// <para><b>The <c>Subject</c> header is RFC 2047-encoded when it is not plain ASCII; <c>From</c>/<c>To</c>
/// are not.</b> RFC 5322 headers are 7-bit ASCII by construction, and a subject line is the one header
/// this item ever populates from user-supplied text that is not already constrained to ASCII (an email
/// address is ASCII by construction; the display names this item generates are fixed, ASCII strings, not
/// visitor input). Skipping this would mean a Cyrillic subject line - the ordinary case for this project's
/// own target customer - going out as raw UTF-8 bytes in a header, which many real mail clients render
/// correctly today but which is not what RFC 5322 actually permits; <c>=?UTF-8?B?...?=</c>
/// (RFC 2047's own <c>encoded-word</c> form) is correct on every client, not merely the common ones.</para>
/// </summary>
internal static class EmailMimeMessageBuilder
{
    private const int Base64LineLength = 76;

    public static string Build(EmailMessageToSend message)
    {
        var headers = new StringBuilder();
        headers.Append("MIME-Version: 1.0\r\n");
        headers.Append($"Date: {FormatDate(message.Date)}\r\n");
        headers.Append($"From: AGO Chat <{message.From}>\r\n");
        headers.Append($"To: <{message.To}>\r\n");
        headers.Append($"Subject: {EncodeHeaderWord(message.Subject)}\r\n");
        headers.Append($"Message-ID: {message.MessageId}\r\n");

        if (message.InReplyTo is { Length: > 0 } inReplyTo)
        {
            headers.Append($"In-Reply-To: {inReplyTo}\r\n");
        }

        if (message.References is { Length: > 0 } references)
        {
            headers.Append($"References: {references}\r\n");
        }

        headers.Append("Content-Type: text/plain; charset=utf-8\r\n");
        headers.Append("Content-Transfer-Encoding: base64\r\n");
        headers.Append("\r\n");

        return headers + WrapBase64(Convert.ToBase64String(Encoding.UTF8.GetBytes(message.Body)));
    }

    /// <summary>
    /// `25-155`: an additive twin of <see cref="Build"/>, not a replacement - produces a real
    /// <c>multipart/alternative</c> body (a <c>text/plain</c> part first, a <c>text/html</c> part second,
    /// the order every mail client expects, so a client with no HTML rendering support falls back cleanly
    /// to the first part it understands) for a caller that has an HTML rendering of the same message to
    /// offer alongside the plain-text one (<see cref="EmailMessageToSend.HtmlBody"/>). Only
    /// <see cref="EmailSmtpClient.SendAsync"/>'s own branch on whether
    /// <see cref="EmailMessageToSend.HtmlBody"/> is set decides which of the two methods runs - that branch
    /// is the only thing routing both <see cref="NotificationMailSender"/>'s own calls (`25-155`) and, since
    /// `25-156`, every <see cref="EmailChannelAdapter"/> call too, to this method instead of <see cref="Build"/>.
    ///
    /// <para>Each part is base64-encoded exactly as <see cref="Build"/>'s own single part already is - the
    /// identical wrap-every-76-characters rule, for the identical reason (this class's own remarks above):
    /// byte-safe with no client-negotiated extension needed, and never itself produces a line starting with
    /// <c>.</c>. The boundary line itself starts with two hyphens, never a dot, but nothing about this
    /// method's own output is exempted from <see cref="EmailSmtpClient"/>'s own dot-stuffing step, which
    /// still runs over the whole payload this method returns exactly as it does for <see cref="Build"/>'s
    /// own output.</para>
    /// </summary>
    public static string BuildMultipartAlternative(EmailMessageToSend message)
    {
        if (message.HtmlBody is not { Length: > 0 } htmlBody)
        {
            throw new InvalidOperationException(
                $"{nameof(BuildMultipartAlternative)} requires {nameof(EmailMessageToSend.HtmlBody)} to be " +
                $"set; use {nameof(Build)} for a plain-text-only message.");
        }

        // A random-per-message boundary, not a fixed constant - RFC 2046's own requirement that a
        // boundary string never collide with anything that can legitimately appear inside either part.
        // Guid.NewGuid() is fine here (unlike in Domain/Application - CLAUDE.md rule 2): this class lives
        // in Infrastructure, the one layer that rule allows it in.
        var boundary = $"AgoChatBoundary{Guid.NewGuid():N}";

        var headers = new StringBuilder();
        headers.Append("MIME-Version: 1.0\r\n");
        headers.Append($"Date: {FormatDate(message.Date)}\r\n");
        headers.Append($"From: AGO Chat <{message.From}>\r\n");
        headers.Append($"To: <{message.To}>\r\n");
        headers.Append($"Subject: {EncodeHeaderWord(message.Subject)}\r\n");
        headers.Append($"Message-ID: {message.MessageId}\r\n");

        if (message.InReplyTo is { Length: > 0 } inReplyTo)
        {
            headers.Append($"In-Reply-To: {inReplyTo}\r\n");
        }

        if (message.References is { Length: > 0 } references)
        {
            headers.Append($"References: {references}\r\n");
        }

        headers.Append($"Content-Type: multipart/alternative; boundary=\"{boundary}\"\r\n");
        headers.Append("\r\n");

        var body = new StringBuilder();
        body.Append($"--{boundary}\r\n");
        body.Append("Content-Type: text/plain; charset=utf-8\r\n");
        body.Append("Content-Transfer-Encoding: base64\r\n");
        body.Append("\r\n");
        body.Append(WrapBase64(Convert.ToBase64String(Encoding.UTF8.GetBytes(message.Body))));
        body.Append("\r\n");
        body.Append($"--{boundary}\r\n");
        body.Append("Content-Type: text/html; charset=utf-8\r\n");
        body.Append("Content-Transfer-Encoding: base64\r\n");
        body.Append("\r\n");
        body.Append(WrapBase64(Convert.ToBase64String(Encoding.UTF8.GetBytes(htmlBody))));
        body.Append("\r\n");
        body.Append($"--{boundary}--");

        return headers.ToString() + body;
    }

    /// <summary>RFC 5322's own required <c>date-time</c> shape (e.g. <c>Tue, 03 Jan 2017 08:00:00
    /// +0000</c>) - not ISO-8601, unlike every other timestamp this codebase transports
    /// (`date-and-time.md`). This is a protocol-mandated exception, not a deviation from that rule's own
    /// intent: the rule is about not inventing a format where a real one is not required, and RFC 5322
    /// requires this exact one for a Date header. <see cref="EmailMessageToSend.Date"/> still always comes
    /// from <c>IClock</c>, never <c>DateTime.Now</c> - only the *rendering* is protocol-specific, the
    /// identical split `date-and-time.md` already draws between storage/transport and the one place a
    /// human-facing render is genuinely required.</summary>
    private static string FormatDate(DateTimeOffset date)
    {
        var offset = date.Offset;
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        var offsetText = $"{sign}{Math.Abs(offset.Hours):00}{Math.Abs(offset.Minutes):00}";
        return date.ToString("ddd, dd MMM yyyy HH:mm:ss ", CultureInfo.InvariantCulture) + offsetText;
    }

    private static string EncodeHeaderWord(string value)
    {
        if (IsAscii(value))
        {
            return value;
        }

        return $"=?UTF-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(value))}?=";
    }

    private static bool IsAscii(string value)
    {
        foreach (var c in value)
        {
            if (c > 0x7F)
            {
                return false;
            }
        }

        return true;
    }

    private static string WrapBase64(string base64)
    {
        if (base64.Length <= Base64LineLength)
        {
            return base64;
        }

        var builder = new StringBuilder(base64.Length + (base64.Length / Base64LineLength) * 2);
        for (var i = 0; i < base64.Length; i += Base64LineLength)
        {
            if (i > 0)
            {
                builder.Append("\r\n");
            }

            var length = Math.Min(Base64LineLength, base64.Length - i);
            builder.Append(base64, i, length);
        }

        return builder.ToString();
    }
}

/// <summary>One outbound email, fully resolved - what <see cref="EmailChannelAdapter"/> builds and hands to
/// <see cref="EmailSmtpClient.SendAsync"/>. <paramref name="MessageId"/> is already formatted as an RFC
/// 5322 <c>Message-ID</c> value (angle brackets included); <paramref name="InReplyTo"/>/
/// <paramref name="References"/> are <see langword="null"/> only in the "should not happen" case
/// <see cref="EmailChannelAdapter"/>'s own remarks describe (no <see cref="Domain.EmailThreadState"/> row
/// for a conversation that must already have received an inbound message before any reply could exist).
///
/// <para><paramref name="HtmlBody"/> is `25-155`'s own addition - non-null for a
/// <see cref="NotificationMailSender"/> call whose caller supplied an HTML rendering alongside
/// <paramref name="Body"/>, and, since `25-156`, also non-null for every message
/// <see cref="EmailChannelAdapter"/> builds (<see cref="Infrastructure.Email.TenantReplyEmailShell"/>'s own
/// tenant-branded reply shell - the one HTML rendering that adapter has ever had reason to attach).
/// <see cref="EmailSmtpClient.SendAsync"/> is the one place that branches on it -
/// <see cref="EmailMimeMessageBuilder.Build"/> when it is <see langword="null"/>,
/// <see cref="EmailMimeMessageBuilder.BuildMultipartAlternative"/> when it is not - so this optional,
/// defaulted parameter still lets a caller with no HTML of its own (a future channel, a test) keep taking
/// the plain-text-only path with no change of its own required.</para>
/// </summary>
public sealed record EmailMessageToSend(
    string From, string To, string Subject, string Body, string MessageId, string? InReplyTo,
    string? References, DateTimeOffset Date, string? HtmlBody = null);
