using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Ago.Chat.Domain;

namespace Ago.Chat.Api.Auth;

internal static class ClaimsPrincipalExtensions
{
    public static SiteId GetSiteId(this ClaimsPrincipal user) =>
        new(Guid.Parse(user.FindFirstValue(AgoClaimTypes.SiteId)!));

    public static VisitorId GetVisitorId(this ClaimsPrincipal user) =>
        new(Guid.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sub)!));

    public static OperatorId GetOperatorId(this ClaimsPrincipal user) =>
        new(Guid.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sub)!));

    /// <summary>`5-03`: <c>true</c> for an operator token, <c>false</c> for a visitor one - only
    /// meaningful on a route that accepted both schemes (see <see cref="AgoClaimTypes.Kind"/>'s own
    /// remarks); every single-scheme hub/endpoint already knows which kind it is from its own
    /// <c>[Authorize(AuthenticationSchemes = ...)]</c> and never needs this.</summary>
    public static bool IsOperator(this ClaimsPrincipal user) =>
        user.FindFirstValue(AgoClaimTypes.Kind) == "operator";
}
