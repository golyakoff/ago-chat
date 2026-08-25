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
    /// <summary>
    /// `17-06`/`adr/0034`: thirty days, and now for a stated reason rather than because nobody
    /// revisited the first number written here. Three things fix it. It is the product promise - the
    /// widget's own <c>getOrCreateVisitorSession</c> reuses a stored token rather than minting a new
    /// identity per page view, so this constant *is* "how long a returning visitor still sees their
    /// own conversation" and nothing else expresses that. Nothing shorter buys security while that
    /// same function has no renewal path: it neither inspects `exp` nor re-mints, so a shorter number
    /// does not narrow an attacker's window so much as break returning visitors sooner. And the
    /// exposure this bounds is narrow by construction - the minting endpoint
    /// (<c>POST /api/v1/visitor-sessions</c>) is public and unauthenticated, so anyone who can read
    /// this token from a page can also mint their own; what the lifetime actually protects is one
    /// visitor's own transcript on a shared or lost device.
    ///
    /// The number drops to seven days the moment silent renewal exists - which is a widget change,
    /// not a change here, and is why `adr/0034` records the dependency instead of shortening this
    /// constant on its own. `17-03` inherits the same number as its key-rotation drain window.
    /// </summary>
    public static readonly TimeSpan VisitorTokenLifetime = TimeSpan.FromDays(30);

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
            new Claim(AgoClaimTypes.Kind, AgoClaimTypes.VisitorKind),
        };

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: JwtSchemes.Visitor,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: now.Add(VisitorTokenLifetime).UtcDateTime,
            signingCredentials: signingCredentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
