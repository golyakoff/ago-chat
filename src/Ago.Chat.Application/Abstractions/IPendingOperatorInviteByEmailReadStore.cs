namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `25-73`: answers "does this signed-in identity's own email have a live, unredeemed invite
/// anywhere" - the one read `OnboardingPage` (`ago-console`) needs to steer an invitee away from the
/// "create your own company" form before they ever submit it, rather than only after a Keycloak
/// registration collision this console has no way to intercept (see this item's own worker report for
/// why the literal "catch Keycloak's own duplicate-email registration error" is not achievable in
/// console code alone). Its own port, not a fourth/fifth method on <see cref="IOperatorInviteRepository"/>/
/// <see cref="IOperatorInviteListReadStore"/> - this is a global, email-keyed question ("anywhere on
/// this deployment"), not a per-site or per-code one either of those already answer.
///
/// <para><b>Not an enumeration risk.</b> The email compared is always the caller's own token claim
/// (`HasPendingOperatorInviteHandler`, called only from a `RequireKeycloakIdentity`-gated route) -
/// nothing here lets one identity ask whether an *arbitrary* email has a pending invite.</para>
/// </summary>
public interface IPendingOperatorInviteByEmailReadStore
{
    Task<bool> AnyPendingForEmailAsync(string email, DateTimeOffset now, CancellationToken cancellationToken);
}
