using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.SetDownloadBlockExemptionAsOwner;
using Ago.Chat.Domain;

namespace Ago.Chat.Api.Owner;

/// <summary>
/// `25-83`: `POST /api/v1/owner/sites/{siteId}/download-block-exemption` - the platform owner's own
/// free, indefinite bypass of a tenant's hard download-block threshold. Its own file, not folded into
/// <see cref="OwnerSuspensionEndpoints"/> or <see cref="OwnerModuleEndpoints"/> - the identical "own
/// file, own Map call" discipline <see cref="OwnerSuspensionEndpoints"/>'s own remarks state for
/// itself: this act is not a suspension and not a module entitlement, it is a third, unrelated
/// owner-only override that happens to share the platform-owner gate.
///
/// <para><b>Gated exclusively by <c>RequirePlatformOwner</c></b> - the entire access-control story,
/// the identical single-gate shape every other owner surface in this codebase already uses.</para>
/// </summary>
public static class OwnerDownloadBlockExemptionEndpoints
{
    public static void MapOwnerDownloadBlockExemptionEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/owner/sites/{siteId:guid}/download-block-exemption", HandleSetAsync)
            .RequireAuthorization("RequirePlatformOwner");
    }

    private static async Task<IResult> HandleSetAsync(
        Guid siteId,
        SetDownloadBlockExemptionRequest request,
        SetDownloadBlockExemptionAsOwnerHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var performedBy = SubjectOf(httpContext);
        if (performedBy is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Token carries no subject claim.");
        }

        var result = await handler.HandleAsync(
            new SetDownloadBlockExemptionAsOwner(new SiteId(siteId), request.Exempt, performedBy, request.Reason),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok();
    }

    /// <summary>The identical "read directly off the validated token, not threaded through" shape
    /// <c>OwnerSuspensionEndpoints.SubjectOf</c>'s own remarks give - recorded, never authorising;
    /// <c>RequirePlatformOwner</c> on the route already decided that.</summary>
    private static string? SubjectOf(HttpContext httpContext) =>
        httpContext.User.FindFirstValue(JwtRegisteredClaimNames.Sub) is { Length: > 0 } sub ? sub : null;

    public sealed record SetDownloadBlockExemptionRequest(bool Exempt, string Reason);
}
