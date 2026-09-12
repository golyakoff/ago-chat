using Ago.Chat.Application.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Infrastructure.Email;

/// <summary>
/// `23-73`: <see cref="INotificationMailSender"/>'s own implementation - reuses
/// <see cref="EmailSmtpClient"/>/<see cref="EmailMimeMessageBuilder"/> exactly as
/// <see cref="EmailChannelAdapter"/> does, just without that adapter's own conversation/thread-state
/// lookups, which this port has no use for (see <see cref="INotificationMailSender"/>'s own remarks on
/// why this is a separate, narrower port rather than a second call shape bolted onto the channel
/// adapter).
///
/// <para><b>The <c>From</c> address.</b> Not <see cref="EmailRecipientAddress"/>'s own
/// <c>support+{siteId}@{domain}</c> subaddress - that shape exists so a visitor's reply threads back to
/// the right site's conversation, which is meaningless here (this mail is never replied to inside this
/// system, and there is no conversation to thread it against). A single, fixed
/// <c>notifications@{domain}</c> local part is used for every tenant-facing administrative mail this
/// port ever sends, deployment-wide - the identical "one shared local part, not a per-site one" shape
/// <see cref="EmailBotApiOptions.SupportLocalPart"/> already establishes, reusing the same
/// <see cref="EmailBotApiOptions.Domain"/> setting rather than a second domain to configure.</para>
/// </summary>
public sealed class NotificationMailSender(
    EmailSmtpClient client, IOptions<EmailBotApiOptions> options, IClock clock,
    ILogger<NotificationMailSender> logger) : INotificationMailSender
{
    /// <summary>The one fixed local part every notification mail is sent from - see this class's own
    /// remarks on why it is not <see cref="EmailBotApiOptions.SupportLocalPart"/>.</summary>
    private const string NotificationLocalPart = "notifications";

    public async Task SendAsync(NotificationMailMessage message, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var from = $"{NotificationLocalPart}@{options.Value.Domain}";
        var outbound = new EmailMessageToSend(
            From: from,
            To: message.To,
            Subject: message.Subject,
            Body: message.Body,
            MessageId: $"<{Guid.NewGuid():N}@{options.Value.Domain}>",
            InReplyTo: null,
            References: null,
            Date: now);

        // `EmailSmtpClient.SendAsync`'s own terminal/transient split (its own remarks) is reused as-is
        // here, unwrapped: a 4xx/connection-stage fault is *thrown* by that client directly, and is left
        // to propagate - InactivityWatchdogJob's own per-candidate try/catch is what turns that into
        // "log and retry next cycle", the same "background sweep, not a request" shape every other
        // Worker job's own outbound call takes (ProcessSubscriptionRenewalHandler's own YooKassa call,
        // for instance). A *permanent* refusal (a 5xx - nonexistent mailbox, relay policy refusal) comes
        // back as EmailSendResult.Refused instead, and is deliberately logged and swallowed here rather
        // than thrown: retrying a mailbox the relay has already permanently refused would never
        // succeed, so retrying it every future sweep cycle forever would only be wasted SMTP traffic -
        // this call still returns normally, and the caller marks the site warned regardless (a bounce is
        // not a reason to warn the same tenant again next cycle; it is a reason someone eventually
        // notices the bounce in this log).
        var result = await client.SendAsync(outbound, cancellationToken);
        if (!result.Success)
        {
            logger.LogWarning(
                "Notification mail to {Recipient} was permanently refused by the relay: {Reason}",
                message.To, result.RefusalReason);
        }
    }
}
