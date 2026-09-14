using System.Globalization;

namespace Ago.Chat.Worker;

/// <summary>
/// `25-83`: the soft-threshold warning mail - "an active notification... an email" (`docs/backlog/25-83-*.md`'s
/// own decision). One bilingual mail, not two, the identical shape <see cref="InactivityWarningMailTemplate"/>'s
/// own remarks state in full for the identical kind of thing (a scheduled, tenant-facing
/// administrative mail with no real operator behind it to resolve a locale from) - reused here rather
/// than the single-locale shape `RouteConversationToModuleHandler`'s own visitor-facing system
/// messages use, because that shape exists to match a visitor's own site locale inside a live
/// conversation, which this mail is not: it is a `Ago.Chat.Worker.DownloadThresholdWatchdogJob`
/// sweep with no conversation and no single visitor to address, the same audience
/// <see cref="InactivityWarningMailTemplate"/>'s own recipients already are.
/// </summary>
internal static class DownloadThresholdWarningMailTemplate
{
    public static (string Subject, string Body) Build(
        string siteName, long bytesOut, long softThresholdBytes, long hardThresholdBytes, string consoleUrl)
    {
        var usedMiB = FormatMiB(bytesOut);
        var softMiB = FormatMiB(softThresholdBytes);
        var hardMiB = FormatMiB(hardThresholdBytes);

        var ruSubject = $"Аккаунт {siteName} в AGO Chat приближается к месячному лимиту скачиваний";
        var ruBody = $"""
            Здравствуйте!

            В этом месяце аккаунт {siteName} скачал уже {usedMiB} МиБ вложений - это больше предупреждающего
            порога вашего тарифа ({softMiB} МиБ).

            Если скачивания продолжатся в том же темпе, по достижении {hardMiB} МиБ аккаунт будет заблокирован
            для скачивания любых вложений - как для сотрудников, так и для клиентов - до начала следующего
            месяца.

            Ничего делать прямо сейчас не обязательно - это предупреждение, а не блокировка. Если лимит
            слишком мал для вашего аккаунта, напишите нам.

            — Команда AGO Chat
            """;

        var enSubject = $"Your AGO Chat account {siteName} is approaching its monthly download limit";
        var enBody = $"""
            Hello,

            This month, account {siteName} has already downloaded {usedMiB} MiB of attachments - past your
            plan's own warning threshold ({softMiB} MiB).

            If downloads continue at this rate, once the account reaches {hardMiB} MiB every attachment
            download will be blocked - for operators and customers alike - until next month begins.

            No action is required right now - this is a warning, not a block. If this limit is too low for
            your account, get in touch with us.

            — The AGO Chat team
            """;

        var subject = $"{ruSubject} / {enSubject}";
        var body = $"{ruBody}\n\n----------\n\n{enBody}\n\n{consoleUrl}".TrimEnd();
        return (subject, body);
    }

    private static string FormatMiB(long bytes) =>
        (bytes / (1024.0 * 1024.0)).ToString("N0", CultureInfo.InvariantCulture);
}
