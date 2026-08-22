using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Ago.Chat.Application.UseCases.ResolveOperatorIdentity;
using Microsoft.AspNetCore.Authentication;

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
/// </summary>
public sealed class OperatorIdentityClaimsTransformation(IServiceScopeFactory scopeFactory) : IClaimsTransformation
{
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

        await using var scope = scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ResolveOperatorIdentityHandler>();
        var identity = await handler.HandleAsync(new ResolveOperatorIdentityQuery(subject), CancellationToken.None);
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
        claimsIdentity.AddClaim(new Claim(AgoClaimTypes.Kind, "operator"));
        principal.AddIdentity(claimsIdentity);
        return principal;
    }
}
