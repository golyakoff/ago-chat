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
    /// `17-08`/`adr/0048`: **seven days, sliding, with no absolute cap.** It was thirty until this
    /// item, and the reason it could not simply be lowered is worth keeping rather than deleting,
    /// because it is the reason this number was allowed to move at all.
    ///
    /// `adr/0034` found that this constant was not really a security parameter: the widget stored the
    /// first token it was ever given and reused it forever - it inspected no `exp` and never
    /// re-minted - so the value *was* "how long a returning visitor still sees their own
    /// conversation" and nothing else expressed that. Lowering it then would have moved the day the
    /// widget silently stops working from day 31 to day 8 while buying nothing, because the minting
    /// endpoint (<c>POST /api/v1/visitor-sessions</c>) is public and unauthenticated: anyone
    /// positioned to read this token off a page can mint their own for the same site. What the
    /// lifetime genuinely bounds is one visitor's own transcript staying reachable from a shared or
    /// lost device, and how long a token outlives the key that signed it (`17-03`).
    ///
    /// `17-07` (the widget) and `17-08` (this endpoint's other half,
    /// <c>POST /api/v1/visitor-sessions/renew</c>) are what separated those two facts. Renewal
    /// issues a full fresh lifetime each time, so an active visitor never expires and the number
    /// stops being the product promise. **No absolute cap**, and `adr/0048` records the trigger that
    /// would add one: the first time a visitor can re-identify themselves without holding the
    /// original token. `17-03`'s key-rotation drain window is derived from this value - seven days
    /// now, not thirty.
    /// </summary>
    public static readonly TimeSpan VisitorTokenLifetime = TimeSpan.FromDays(7);

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
