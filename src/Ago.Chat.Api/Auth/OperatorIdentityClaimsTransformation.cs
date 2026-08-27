using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Ago.Chat.Application.UseCases.ResolveOperatorIdentity;
using Ago.Chat.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;

namespace Ago.Chat.Api.Auth;

/// <summary>
/// `5-05`/`adr/0022`: a validated Keycloak token proves *who this person is to Keycloak* - its `sub`
/// means nothing to this project on its own. Runs after JWT validation (the framework's own
/// <see cref="IClaimsTransformation"/> extension point, registered once, globally) and, for a
/// principal that looks like it came from Keycloak rather than <c>Ago.Chat.Api</c> itself, resolves
/// `sub` against the `operators` table (<see cref="ResolveOperatorIdentityHandler"/>) and adds
/// <see cref="AgoClaimTypes.OperatorId"/>/<see cref="AgoClaimTypes.SiteId"/> onto the principal.
///
/// "Looks like it came from Keycloak" is "does not already carry a `site_id` claim" - the Visitor
/// scheme's self-issued tokens always do (<c>JwtTokenService.IssueVisitorToken</c>), a Keycloak token
/// never can (Keycloak has no notion of this project's `site_id`). Checking for the *absence* of a
/// claim this project itself controls the presence of is simpler and more certain than trying to
/// infer which authentication scheme actually validated the principal.
///
/// A `sub` that resolves to no operator adds nothing - the request stays authenticated as a Keycloak
/// identity, but `Ago.Chat.Api` has never heard of it as an operator. `Program.cs`'s
/// `RequireOperatorIdentity` policy (`RequireClaim(AgoClaimTypes.OperatorId)`) is what turns that into
/// a clean rejection, rather than `ClaimsPrincipalExtensions.GetOperatorId` throwing on a missing claim
/// deep inside a handler.
///
/// <para><b>`13-07`/`adr/0068`: the active-site signal, and why it has two forms.</b> An ordinary
/// REST call carries it as the <see cref="ActiveSiteHeaderName"/> request header - the natural place
/// for a client-supplied signal on a stateless HTTP request. The SignalR hub connection cannot use
/// that reliably: this project already works around browsers' inability to attach a custom header (or
/// even `Authorization`) to a WebSocket upgrade by putting the bearer token in the query string
/// instead (`Program.cs`'s own <c>HubTokenFromQueryString</c>, the standard ASP.NET Core SignalR
/// pattern) - the identical constraint applies to any other signal a hub client wants the server to
/// see, so this reads <see cref="ActiveSiteQueryParameterName"/> from the query string as a fallback
/// when the header is absent, and `ago-console`'s <c>operatorConnection.ts</c> is what actually sets
/// it that way for the hub URL. Verified against a real hub connection
/// (<c>ActiveSiteHubResolutionTests</c>, `Ago.Chat.Integration.Tests`) - not assumed from how the
/// token already does it.</para>
///
/// <para>Runs once per HTTP request for an ordinary REST call, exactly as before this item - so an
/// operator switching tenancies takes effect on their very next call. For a hub connection, this runs
/// once, at the connection's own handshake (negotiate, then the transport-connect request), because
/// that is the only point in a long-lived SignalR connection's lifetime where a fresh HTTP request -
/// and therefore a fresh authentication pass - occurs; a hub method invoked afterward rides the same
/// already-authenticated `Context.User` for as long as the connection stays open. A tenant switch for
/// an open hub connection is therefore a reconnect, not a new code path - `13-07`'s own Scope already
/// says so, and this is why.</para>
///
/// <para>A missing or malformed signal (no header, no query parameter, or a value that does not parse
/// as a <see cref="SiteId"/>) is treated as "no site requested" - today's own default resolution,
/// never an error. `adr/0068`'s own "Negative consequences" paragraph states why that is a deliberate,
/// safe trade rather than an oversight: this signal can only ever *narrow* what a request resolves to,
/// so failing to read it can only fail to narrow, never widen, access.</para>
/// </summary>
public sealed class OperatorIdentityClaimsTransformation(
    IServiceScopeFactory scopeFactory, IHttpContextAccessor httpContextAccessor) : IClaimsTransformation
{
    /// <summary>`13-07`/`adr/0068`: the header name the ADR itself names as an example and this item
    /// finalises. Used consistently by every REST caller in `ago-console` (`operatorsApi.ts` and
    /// every other `src/api/*.ts` module) - see each call site's own remarks.</summary>
    public const string ActiveSiteHeaderName = "X-Ago-Active-Site";

    /// <summary>The hub-handshake fallback - see this class's own remarks on why headers alone are
    /// not reliable for a WebSocket upgrade. `ago-console`'s <c>operatorConnection.ts</c> appends this
    /// to the hub URL's query string, the same place the bearer token already rides
    /// (`Program.cs`'s <c>HubTokenFromQueryString</c>).</summary>
    public const string ActiveSiteQueryParameterName = "activeSite";

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not { IsAuthenticated: true } || principal.HasClaim(c => c.Type == AgoClaimTypes.SiteId))
        {
            return principal;
        }

        var subject = principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (string.IsNullOrEmpty(subject))
        {
            return principal;
        }

        var requestedSiteId = ReadRequestedSiteId();

        await using var scope = scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ResolveOperatorIdentityHandler>();
        var identity = await handler.HandleAsync(
            new ResolveOperatorIdentityQuery(subject, requestedSiteId), CancellationToken.None);
        if (identity is null)
        {
            return principal;
        }

        var claimsIdentity = new ClaimsIdentity();
        claimsIdentity.AddClaim(new Claim(AgoClaimTypes.OperatorId, identity.OperatorId.Value.ToString()));
        claimsIdentity.AddClaim(new Claim(AgoClaimTypes.SiteId, identity.SiteId.Value.ToString()));
        // `5-03`'s multi-scheme AttachmentEndpoints reads this to tell an operator token from a
        // visitor one on a route that accepts either - a self-issued token carried it directly
        // (JwtTokenService.Issue); a Keycloak token never will on its own, so this is the one place
        // that adds it for the operator side now.
        claimsIdentity.AddClaim(new Claim(AgoClaimTypes.Kind, AgoClaimTypes.OperatorKind));
        principal.AddIdentity(claimsIdentity);
        return principal;
    }

    /// <summary>Header first, then the hub's own query-string fallback - see this class's own remarks
    /// on why both exist. <see langword="null"/> for anything that is absent or does not parse as a
    /// <see cref="SiteId"/> (a bare <see cref="Guid"/>) - treated as "no site requested", never as an
    /// error (this class's own remarks on why that is the safe direction to fail in).</summary>
    private SiteId? ReadRequestedSiteId()
    {
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is null)
        {
            return null;
        }

        var raw = httpContext.Request.Headers[ActiveSiteHeaderName].FirstOrDefault();
        if (string.IsNullOrEmpty(raw))
        {
            raw = httpContext.Request.Query[ActiveSiteQueryParameterName].FirstOrDefault();
        }

        return !string.IsNullOrEmpty(raw) && Guid.TryParse(raw, out var siteId) ? new SiteId(siteId) : null;
    }
}
