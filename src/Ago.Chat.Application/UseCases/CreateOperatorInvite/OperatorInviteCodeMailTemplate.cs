using Ago.Chat.Application.Emailing;

namespace Ago.Chat.Application.UseCases.CreateOperatorInvite;

/// <summary>
/// `25-90`: the invite code's own second, independent delivery channel. `docs/backlog/25-90-*.md`'s own
/// Scope investigated putting the plaintext code into Keycloak's `execute-actions-email` template
/// (`OperatorInviteEmailProvisioner`'s own call) and found no parameter through which that template could
/// ever receive it - the only way to give the template something to render would have been a Keycloak
/// user attribute, which the author explicitly rejected (the code must never sit in Keycloak's own
/// storage). This template is the copy for the alternative that item settled on instead: a second email,
/// sent by <c>ago-chat</c> itself through <see cref="Ago.Chat.Application.Abstractions.INotificationMailSender"/>,
/// entirely outside Keycloak's relay.
///
/// <para><b>One bilingual mail, not a locale switch.</b> This port's only two existing callers -
/// <c>Ago.Chat.Worker.InactivityWarningMailTemplate</c> and <c>Ago.Chat.Worker.DownloadThresholdWarningMailTemplate</c>
/// - both render Russian above English in a single message rather than selecting one language per
/// recipient, because neither job has one single, reliable "this recipient's language" value to switch
/// on. <see cref="Ago.Chat.Application.UseCases.CreateOperatorInvite.CreateOperatorInviteHandler"/> does
/// have one (the site's own <see cref="Ago.Chat.Domain.Locale"/>, already threaded to
/// <see cref="Ago.Chat.Infrastructure.Keycloak.OperatorInviteEmailProvisioner"/>'s Keycloak locale
/// attribute) - but this class deliberately does not branch on it anyway, and instead follows this port's
/// own established shape as-is: "the convention this codebase's other locale-aware transactional copy
/// already uses" (this item's own Scope) is this exact bilingual-concatenation shape, not the separate
/// "switch on one known Locale" pattern <c>OperatorInviteEmailProvisioner.KeycloakLocaleCode</c> uses for
/// a different purpose (choosing which of Keycloak's *own* templates to render, not composing this
/// backend's own copy). Reusing the pattern this port's callers already settled on beats introducing a
/// second, novel one for a single new caller.</para>
///
/// <para>Lives beside <see cref="CreateOperatorInviteHandler"/>, in <c>Ago.Chat.Application</c>, not in
/// <c>Ago.Chat.Infrastructure.Email</c> alongside <c>NotificationMailSender</c> - the identical "the
/// sender knows how to deliver, the caller decides what to say" split
/// <c>Ago.Chat.Worker.InactivityWarningMailTemplate</c>'s own doc comment already draws for this same
/// port's other callers, just one layer up: this handler is the caller here, so this handler's own copy
/// lives beside it.</para>
/// </summary>
internal static class OperatorInviteCodeMailTemplate
{
    /// <summary>
    /// `25-155`: <paramref name="Body"/> is byte-for-byte the same string this method has always
    /// returned - the plain-text wording is unchanged, only a third, HTML rendering of the identical
    /// content through <see cref="EmailHtmlShell"/> (`docs/backlog/25-155-*.md`'s own "same wording, new
    /// rendering") is new.
    /// </summary>
    public static (string Subject, string Body, string HtmlBody) Build(string code, string redeemUrl)
    {
        const string ruSubject = "Резервный код приглашения AGO Chat";
        var ruBody = $"""
            Здравствуйте!

            Это отдельное, резервное письмо с кодом приглашения в AGO Chat — на случай, если письмо со
            ссылкой для активации аккаунта не дошло или потерялось. Оба письма независимы друг от друга:
            для активации аккаунта достаточно любого одного из них.

            Ваш код приглашения (скопируйте его целиком):

            {code}

            Что делать: создайте аккаунт (или войдите, если он уже есть), откройте страницу «Активировать
            код приглашения» ({redeemUrl}) и вставьте этот код в поле «Код приглашения».

            — Команда AGO Chat
            """;

        const string enSubject = "Your AGO Chat backup invite code";
        var enBody = $"""
            Hello,

            This is a separate, backup email carrying your AGO Chat invite code, in case the account-
            activation email did not arrive or got lost. The two emails are independent of each other -
            either one on its own is enough to activate the account.

            Your invite code (copy it in full):

            {code}

            What to do: create your account (or sign in if you already have one), open the "Redeem an
            invite code" page ({redeemUrl}), and paste this code into the "Invite code" field.

            — The AGO Chat team
            """;

        var subject = $"{ruSubject} / {enSubject}";
        var body = $"{ruBody}\n\n----------\n\n{enBody}";

        var htmlBody = EmailHtmlShell.Render(new EmailShellContent(
            Heading: "Ваш резервный код приглашения / Your backup invite code",
            HeroIcon: "\U0001F511", // key
            BodyParagraphs:
            [
                "Здравствуйте! Это отдельное, резервное письмо с кодом приглашения в AGO Chat — на случай, " +
                "если письмо со ссылкой для активации аккаунта не дошло или потерялось.",
                $"Ваш код приглашения (скопируйте его целиком): {code}",
                "Что делать: создайте аккаунт (или войдите, если он уже есть), откройте страницу «Активировать " +
                "код приглашения» и вставьте этот код в поле «Код приглашения».",
                "Hello, this is a separate, backup email carrying your AGO Chat invite code, in case the " +
                "account-activation email did not arrive or got lost.",
                $"Your invite code (copy it in full): {code}",
                "What to do: create your account (or sign in if you already have one), open the \"Redeem an " +
                "invite code\" page, and paste this code into the \"Invite code\" field.",
            ],
            CallToAction: new EmailShellCallToAction("Открыть страницу активации / Open the redeem page", redeemUrl),
            SecondaryNote:
                "Оба письма независимы друг от друга — для активации аккаунта достаточно любого одного из " +
                "них. / The two emails are independent of each other — either one on its own is enough."));

        return (subject, body, htmlBody);
    }
}
