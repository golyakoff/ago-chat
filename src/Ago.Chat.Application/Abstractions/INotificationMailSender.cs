namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `23-73`: one transactional, tenant-facing email - a deliberately narrower shape than
/// <see cref="Ago.Chat.Domain.ChannelKind.Email"/>'s own channel adapter. That adapter replies to a
/// *visitor*, inside a conversation, threaded against an <see cref="Ago.Chat.Domain.EmailThreadState"/>
/// row that only exists because a real inbound message created it. This port has none of that shape to
/// reuse: the recipient is an operator/tenant contact address (an <see cref="Ago.Chat.Domain.Operator"/>'s
/// own <see cref="Ago.Chat.Domain.Operator.Email"/>), there is no conversation and no thread to reference,
/// and the caller (<c>Ago.Chat.Worker.InactivityWatchdogJob</c>) is not relaying anything a visitor sent -
/// it is telling a tenant something the platform itself decided. Folding this into
/// <see cref="Ago.Chat.Domain.ChannelKind.Email"/>'s own adapter would mean inventing a fake
/// conversation/thread for a message that is not a reply to anyone, purely to satisfy an interface shaped
/// for a different problem.
///
/// <para><b>Implemented in <c>Ago.Chat.Infrastructure.Email</c> by reusing <c>EmailSmtpClient</c>/
/// <c>EmailMimeMessageBuilder</c> directly</b> - the same hand-rolled SMTP send primitive the channel
/// adapter already uses, not a second SMTP client and not a NuGet mail library
/// (<c>EmailSmtpClient</c>'s own remarks on why hand-rolling beat MailKit for this deployment's one real
/// relay apply identically to a second, purely administrative send).</para>
/// </summary>
public interface INotificationMailSender
{
    Task SendAsync(NotificationMailMessage message, CancellationToken cancellationToken);
}

/// <summary>One outbound notification email, already fully composed (subject and body resolved, no
/// further templating left to do) - the caller decides *what* to say, this port only decides *how* to
/// get it there.
///
/// <para><paramref name="HtmlBody"/> is `25-155`'s own addition, optional and defaulted to
/// <see langword="null"/> so every existing positional call
/// (<c>new NotificationMailMessage(to, subject, body)</c>) keeps compiling unchanged.
/// <paramref name="Body"/> stays the plain-text rendering, not renamed to something like
/// <c>PlainTextBody</c>, precisely so it stays that unchanged rendering - the same wording this item's
/// own Scope requires ("same wording, new rendering"), now threaded through to
/// <c>Ago.Chat.Infrastructure.Email.NotificationMailSender</c> alongside an HTML rendering of the
/// identical content, built through <c>Ago.Chat.Application.Emailing.EmailHtmlShell</c>.</para>
/// </summary>
public sealed record NotificationMailMessage(string To, string Subject, string Body, string? HtmlBody = null);
