using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>`25-90`: the second, independent invite-code channel's own test double -
/// <c>Ago.Chat.Integration.Tests.InactivityWatchdogJobTests</c>'s own private <c>FakeNotificationMailSender</c>
/// is the identical shape (record everything sent), just promoted to a shared Application-layer fake here
/// because <c>CreateOperatorInviteHandlerTests</c> is the first Application-layer test to need one -
/// `INotificationMailSender`'s only prior callers were Worker jobs.
///
/// <para><paramref name="throwOnSend"/> optionally makes <see cref="SendAsync"/> throw instead of
/// recording - the same transient/connection-stage fault <c>NotificationMailSender</c>'s own doc comment
/// says it throws rather than swallows, used to prove <c>CreateOperatorInviteHandler</c>'s own
/// catch-and-log posture for this port (`docs/backlog/25-90-*.md`'s own Scope: the two invite emails
/// "are independent... neither should depend on the other succeeding").</para></summary>
public sealed class FakeNotificationMailSender(Exception? throwOnSend = null) : INotificationMailSender
{
    public List<NotificationMailMessage> Sent { get; } = [];

    public Task SendAsync(NotificationMailMessage message, CancellationToken cancellationToken)
    {
        if (throwOnSend is not null)
        {
            throw throwOnSend;
        }

        Sent.Add(message);
        return Task.CompletedTask;
    }
}
