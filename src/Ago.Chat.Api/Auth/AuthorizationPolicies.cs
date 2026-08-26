using Microsoft.AspNetCore.Authorization;

namespace Ago.Chat.Api.Auth;

/// <summary>
/// The authorization policies this codebase builds inline on a route rather than naming in
/// `Program.cs`'s <c>AddAuthorization</c> block. They live here, as methods, for the reason `17-06`
/// found: a rule a test has to be able to exercise, and a lambda inside
/// <c>MapAttachmentEndpoints</c> is not reachable from one.
///
/// The three named policies (`RequireOperatorIdentity`, `RequireKeycloakIdentity`,
/// `RequirePlatformOwner`) stay in `Program.cs` - they are wired by name from `[Authorize]`
/// attributes and endpoint metadata, which is exactly what the named-policy registry is for. These
/// are applied to a route or a route group by delegate, so a name would buy nothing - and, for
/// <see cref="NotThePlatformOwner"/>, a name would actively cost something: a named policy is
/// resolved from whatever <c>AddAuthorization</c> block the *host* configured, so the rule travels
/// with the host rather than with the route. A delegate travels with the route, which means no host
/// can map `POST /api/v1/sites` without also getting this refusal - including the <c>TestServer</c>
/// hosts in this suite, which transcribe `Program.cs`'s named policies by hand.
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

    /// <summary>
    /// `12-04`: `10-02`'s registration bootstrap (`POST /api/v1/sites`) is not for the platform
    /// owner, and this is the check that says so - <b>server-side, where the row is actually
    /// committed</b>.
    ///
    /// <para><b>What it prevents.</b> That endpoint commits a `Site`, both built-in roles, an
    /// `Operator` row for the caller's `sub` and its role assignments, in one transaction, and this
    /// product has no way to undo any of it. `adr/0032` gives the platform owner no `operators` row
    /// deliberately, so the owner would be *bootstrapping itself into a tenant it can never leave* -
    /// on the live deployment, a state only a hand-written `DELETE` against production could
    /// reverse. The console hides the form from an owner too (`CallbackPage`, `OnboardingPage`), but
    /// hiding a form is a suggestion; a bookmark, a back button or a second tab all reach the
    /// endpoint directly, and only this refusal is between them and the transaction.</para>
    ///
    /// <para><b>Why the policy layer rather than <c>RegisterSiteHandler</c>.</b> `adr/0016` puts
    /// authorization checks in `Application` because they resolve *this system's own* site-scoped
    /// data. Recognising the platform owner reads none of it - it is a property of the validated
    /// token, decided before any use case runs, exactly as `adr/0032` states and exactly where
    /// `RequireOperatorIdentity`/`RequireKeycloakIdentity` already sit. The alternative, a
    /// `CallerIsPlatformOwner` flag on the <c>RegisterSite</c> command, would make `Application`
    /// depend on a fact it cannot verify and can only be handed correctly, and would spend two
    /// rate-limit buckets and a database round trip before refusing. It is also the layer `17-06`
    /// closed the same ambiguity at on the attachment route - <see cref="EitherTokenKind"/>, one
    /// method above.</para>
    ///
    /// <para><b>It is additive, not a replacement.</b> The route keeps
    /// `RequireAuthorization("RequireKeycloakIdentity")`; this is a second policy on the same
    /// endpoint, and ASP.NET Core combines every policy in an endpoint's metadata into one that must
    /// pass as a whole. So the identity gate `adr/0028` argued for is untouched and this states one
    /// narrow exclusion on top of it, rather than restating the first rule and inviting the two to
    /// drift.</para>
    ///
    /// <para><b>The refusal is a bare `403`</b>, with no problem+json `type` code, because a policy
    /// failure has no body. Accepted knowingly: the console never routes an owner to that form
    /// (`CallbackPage` sends them to `/owner`) and explains itself in words if one arrives there
    /// anyway, so this is the backstop for a caller who should not be here at all - not a message
    /// anybody is meant to read. Giving it a readable body would mean moving the decision into the
    /// endpoint handler, which is the layer trade above, made the other way for a nicer error.</para>
    /// </summary>
    public static void NotThePlatformOwner(AuthorizationPolicyBuilder policy) => policy
        .AddAuthenticationSchemes(JwtSchemes.Operator)
        .RequireAuthenticatedUser()
        // The one implementation of the rule, shared with `RequirePlatformOwner`'s own handler -
        // `PlatformOwnerRealmRole`'s remarks on why a second copy is the thing `12-04` exists to
        // avoid.
        .RequireAssertion(context => !PlatformOwnerRealmRole.IsHeldBy(context.User));
}
