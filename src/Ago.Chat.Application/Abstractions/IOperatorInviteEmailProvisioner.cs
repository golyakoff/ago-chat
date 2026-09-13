using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `25-73`: creates or reuses a Keycloak identity for a real operator invite and sends Keycloak's own
/// action-token email - the mechanism that actually delivers this invite now, replacing the admin
/// copying a link themselves (`23-70`). Its own port in <c>Application/Abstractions</c>, not a call
/// inline in <c>CreateOperatorInviteHandler</c>: Keycloak's Admin API is an external resource
/// (`CLAUDE.md` rule 2 - no `HttpClient` inside Application), so this follows the same "declared here,
/// implemented in an Infrastructure project" split <see cref="Application.UseCases.MintDemoTenant.IDemoIdentityProvisioner"/>
/// already establishes for the one other thing this codebase ever writes to Keycloak.
///
/// <para><b>Its own new type in <c>Ago.Chat.Infrastructure.Keycloak</c>, deliberately not
/// <c>KeycloakDemoIdentityProvisioner</c> reused or extended.</b> That class serves a same-shaped but
/// semantically different case: a synthetic `.invalid` address nothing can ever reach, no required
/// actions, no user lookup (its own doc comment: "the caller chooses the subject id"). A real invite
/// needs the opposite of every one of those - a real address Keycloak must actually relay mail to,
/// `UPDATE_PASSWORD`/`UPDATE_PROFILE` required actions (there is no name to give at creation time), and
/// a genuine create-or-find-by-email round trip for the "this email already has an identity somewhere on
/// the realm" case this item's own design treats as expected, not an error. Folding both into one type
/// would make every future reader of either untangle which branch belongs to which caller.</para>
/// </summary>
public interface IOperatorInviteEmailProvisioner
{
    Task<OperatorInviteProvisionOutcome> ProvisionAndSendAsync(
        OperatorInviteProvisionRequest request, CancellationToken cancellationToken);
}

/// <summary><paramref name="RedirectUri"/> already carries this invite's own <paramref name="Code"/> as
/// a query parameter (built by the caller, not this port) - see <c>OperatorInviteEmailProvisioner</c>'s
/// own remarks for why the code has to travel inside the URL itself rather than through
/// `sessionStorage`, the mechanism this item replaces. <paramref name="Lifespan"/> is
/// <c>OperatorInviteOptions.ValidFor</c>, passed through unchanged rather than re-read from
/// configuration in the Infrastructure layer - one value, one place it is decided, so the invite's own
/// `expires_at` and the action token Keycloak mints cannot silently disagree (this item's own point 2).</summary>
public sealed record OperatorInviteProvisionRequest(
    string Email, string Code, TimeSpan Lifespan, string RedirectUri, Locale Locale);

/// <summary>Two outcomes, not a `Result&lt;T&gt;` - both are real, expected shapes of "the invite was
/// created", the same "a closed hierarchy the compiler forces every switch to handle" reasoning
/// <see cref="OperatorInviteRedemptionResult"/>'s own remarks give. A genuine infrastructure fault
/// (Keycloak unreachable, an unexpected 5xx while creating or looking up the user) is thrown instead,
/// matching <see cref="Application.UseCases.MintDemoTenant.IDemoIdentityProvisioner"/>'s own "no retry
/// loop/circuit breaker inside the type itself" convention - the resilience pipeline wrapping the
/// `HttpClient` decides what to do with that, not this port's own caller.</summary>
public abstract record OperatorInviteProvisionOutcome
{
    private OperatorInviteProvisionOutcome()
    {
    }

    /// <summary>Keycloak accepted the action-token request and relayed the email - the common case.</summary>
    public sealed record Sent : OperatorInviteProvisionOutcome;

    /// <summary>Keycloak resolved (or created) the user and accepted the `execute-actions-email` call,
    /// but its own realm SMTP relay failed to deliver it. Deliberately not thrown, unlike an unexpected
    /// fault earlier in the same request: this item's own point 6 requires the failure to reach the
    /// console attached to the invite it belongs to, not to abort invite creation outright - the invite
    /// still exists, so a later resend (out of this item's own scope) has something to act on.
    /// <paramref name="SmtpErrorCode"/> is best-effort - Keycloak's Admin REST API answers this call
    /// with its own HTTP status and body, not a passthrough of the underlying SMTP server's own
    /// response code; see <c>OperatorInviteEmailProvisioner</c>'s own remarks for what is actually
    /// captured here and why finer detail could not be verified against a live realm.</summary>
    public sealed record SendFailed(string SmtpErrorCode) : OperatorInviteProvisionOutcome;
}
