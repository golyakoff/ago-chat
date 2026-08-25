using Microsoft.AspNetCore.Authorization;

namespace Ago.Chat.Api.Auth;

/// <summary>
/// The one authorization policy this codebase builds inline on a route rather than naming in
/// `Program.cs`'s <c>AddAuthorization</c> block. It lives here, as a method, for the reason `17-06`
/// found: it is the rule a test has to be able to exercise, and a lambda inside
/// <c>MapAttachmentEndpoints</c> is not reachable from one.
///
/// The three named policies (`RequireOperatorIdentity`, `RequireKeycloakIdentity`,
/// `RequirePlatformOwner`) stay in `Program.cs` - they are wired by name from `[Authorize]`
/// attributes and endpoint metadata, which is exactly what the named-policy registry is for. This one
/// is applied to a route group by delegate, so a name would buy nothing.
/// </summary>
internal static class AuthorizationPolicies
{
    /// <summary>
    /// `5-03`'s shared attachment routes: either a visitor token or an operator token, and nothing
    /// else. The scheme list alone was the whole rule until `17-06`; the
    /// <see cref="AgoClaimTypes.Kind"/> requirement is what closes the third state.
    ///
    /// That third state is worth stating precisely, because it is the reason this method exists.
    /// `kind` is not decoration on these routes - it is the branch condition every handler runs on
    /// (<see cref="ClaimsPrincipalExtensions.IsOperator"/>), and `IsOperator()` returning
    /// <c>false</c> was read as "therefore a visitor". Since `10-01`/`adr/0028` opened the realm to
    /// public self-registration there is a third kind of principal that is neither: a
    /// signature/audience/lifetime-valid Keycloak token whose `sub` matches no `operators` row. It
    /// authenticates on the Operator scheme, gains no `kind` claim (only
    /// <see cref="OperatorIdentityClaimsTransformation"/>'s *successful* path adds one), and so fell
    /// into the visitor branch, where <see cref="ClaimsPrincipalExtensions.GetVisitorId"/> parsed
    /// Keycloak's own `sub` GUID as a <c>VisitorId</c>. Nothing was actually reachable that way - the
    /// participant/ownership checks inside each handler compare that id against the conversation's
    /// real visitor and never matched - but "the wrong branch, saved by the check after it" is not how
    /// a scheme boundary should hold, and the same reasoning would have to be re-derived by every
    /// future reader of a handler that says `IsOperator() ? ... : ...`.
    ///
    /// Requiring the claim to be one of two known values is the narrowest fix and the one that fails
    /// closed: a principal carrying neither is a 403 at the policy layer - the same layer `adr/0028`
    /// already chose for distinguishing two things one scheme can authenticate - rather than a
    /// silently-misclassified request. `12-01`'s platform-owner token, which also has no `operators`
    /// row, lands there too, which is correct: an owner is not a party to any conversation.
    /// </summary>
    public static void EitherTokenKind(AuthorizationPolicyBuilder policy) => policy
        .AddAuthenticationSchemes(JwtSchemes.Visitor, JwtSchemes.Operator)
        .RequireAuthenticatedUser()
        .RequireClaim(AgoClaimTypes.Kind, AgoClaimTypes.VisitorKind, AgoClaimTypes.OperatorKind);
}
