using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;
using Microsoft.IdentityModel.Tokens;

namespace Ago.Chat.Api.Auth;

/// <summary>
/// Issues both token kinds from the one signing key `Program.cs` configures - auth is a host concern
/// (clean-architecture.md: "the only place that knows concrete implementations"), so this lives here,
/// not in Module or Application.
/// </summary>
public sealed class JwtTokenService(SigningCredentials signingCredentials, string issuer, IClock clock)
{
    /// <summary>The Operator scheme's own counterpart, `IssueOperatorToken`, was deleted in `5-05` -
    /// `adr/0022` replaces it outright with real OIDC (Keycloak issues operator tokens now), never
    /// evolves it. Visitors were never behind that stub, so this is untouched.</summary>
    public string IssueVisitorToken(VisitorId visitorId, SiteId siteId)
    {
        var now = clock.UtcNow;
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, visitorId.Value.ToString()),
            new Claim(AgoClaimTypes.SiteId, siteId.Value.ToString()),
            // `5-03`: AttachmentEndpoints accepts either scheme on one route and needs to tell them
            // apart from inside the handler.
            new Claim(AgoClaimTypes.Kind, "visitor"),
        };

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: JwtSchemes.Visitor,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: now.AddDays(30).UtcDateTime,
            signingCredentials: signingCredentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
