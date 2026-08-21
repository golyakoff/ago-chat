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
    public string IssueVisitorToken(VisitorId visitorId, SiteId siteId) =>
        Issue(JwtSchemes.Visitor, visitorId.Value.ToString(), siteId, TimeSpan.FromDays(30));

    public string IssueOperatorToken(OperatorId operatorId, SiteId siteId) =>
        Issue(JwtSchemes.Operator, operatorId.Value.ToString(), siteId, TimeSpan.FromHours(8));

    private string Issue(string audience, string subject, SiteId siteId, TimeSpan lifetime)
    {
        var now = clock.UtcNow;
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, subject),
            new Claim(AgoClaimTypes.SiteId, siteId.Value.ToString()),
        };

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: now.Add(lifetime).UtcDateTime,
            signingCredentials: signingCredentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
