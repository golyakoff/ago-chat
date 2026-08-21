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
}
