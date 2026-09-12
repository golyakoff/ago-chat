using System.Globalization;

namespace Ago.Chat.Worker;

/// <summary>
/// `23-73`: the drafted bilingual warning mail, verbatim - copied from the backlog item's own "The
/// warning mail, drafted" section, not rewritten (the item's own instruction to whoever builds this).
/// One email, not two: <see cref="Build"/> concatenates the Russian block above the English one, the
/// same "one bilingual mail" reading the backlog item's own drafted section gives (two language blocks
/// under one heading, not two separate deliverables) - the reasoning
/// <see cref="Ago.Chat.Application.Abstractions.INotificationMailSender"/> exists as a single-message
/// port at all, rather than one call per language.
///
/// <para>Lives in <c>Ago.Chat.Worker</c>, beside <see cref="InactivityWatchdogJob"/>, rather than in
/// <c>Ago.Chat.Infrastructure.Email</c> alongside <c>NotificationMailSender</c>: the same "the sender
/// knows how to deliver a mail, the caller decides what it says" split
/// <c>Ago.Chat.Infrastructure.Email.EmailMessageToSend</c>'s own shape already draws for the channel
/// adapter (that class carries a fully-resolved subject/body, never a template) - this job is the one
/// and only place this copy is ever needed, so a template class is not shared Infrastructure, it is
/// this job's own business logic, the same "the Worker job embeds its own background-flow logic
/// directly, with no intervening Application handler" idiom every other job in this project already
/// follows (`SiteErasureJob`/`DemoTenantExpiryJob`, neither of which route through an Application-layer
/// handler either).</para>
/// </summary>
internal static class InactivityWarningMailTemplate
{
    public static (string Subject, string Body) Build(
        string siteName, int daysRemaining, DateTimeOffset deletionDate, string loginUrl)
    {
        var deletionDateText = deletionDate.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " UTC";

        var ruSubject = $"Аккаунт {siteName} в AGO Chat будет удалён через {daysRemaining} дн.";
        var ruBody = $"""
            Здравствуйте!

            Мы не видим активности в аккаунте {siteName} уже почти три месяца: никто не заходил в Офис,
            и ни один оператор не отвечал клиентам через AGO Chat.

            Если ничего не изменится, {deletionDateText} аккаунт будет удалён безвозвратно — вместе с историей
            переписки, контактами клиентов, вложениями и настройками.

            Чтобы сохранить аккаунт, достаточно одного из двух действий до этой даты:
            — зайти в Офис: {loginUrl}
            — ответить хотя бы одному клиенту через AGO Chat.

            Если вы уже не пользуетесь сервисом и удаление ожидаемо — ничего делать не нужно.

            — Команда AGO Chat
            """;

        var enSubject = $"Your AGO Chat account {siteName} will be deleted in {daysRemaining} days";
        var enBody = $"""
            Hello,

            We haven't seen any activity on the {siteName} account for close to three months: nobody has
            signed into the Office console, and no operator has replied to a customer through AGO Chat.

            If nothing changes, the account will be permanently deleted on {deletionDateText} — along with its
            conversation history, customer contacts, attachments and settings.

            To keep the account, either of these before that date is enough:
            — sign into the Office console: {loginUrl}
            — reply to at least one customer through AGO Chat.

            If you've already stopped using the service and this deletion is expected, there's nothing you need
            to do.

            — The AGO Chat team
            """;

        var subject = $"{ruSubject} / {enSubject}";
        var body = $"{ruBody}\n\n----------\n\n{enBody}";
        return (subject, body);
    }
}
