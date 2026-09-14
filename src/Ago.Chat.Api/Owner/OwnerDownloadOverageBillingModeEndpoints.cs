using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.SetDownloadOverageBillingModeAsOwner;
using Ago.Chat.Domain;

namespace Ago.Chat.Api.Owner;

/// <summary>
/// `25-84`: `POST /api/v1/owner/sites/{siteId}/download-overage-billing-mode` - the platform owner's
/// own per-tenant choice of *when* that tenant commits to paying the download-overage meter. Its own
/// file beside <see cref="OwnerDownloadBlockExemptionEndpoints"/>, not folded into it - the identical
/// "own file, own Map call" discipline that file's own remarks already state for itself, and the two
/// are genuinely different acts: one waives the charge entirely, the other decides how it is collected.
///
/// <para><b>Gated exclusively by <c>RequirePlatformOwner</c></b> - the entire access-control story, and
/// the reason `25-84`'s own Out of scope ("any tenant-facing self-service control over the toggle") is
/// enforced structurally rather than by a console that happens not to show a button.</para>
/// </summary>
public static class OwnerDownloadOverageBillingModeEndpoints
{
    public static void MapOwnerDownloadOverageBillingModeEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/owner/sites/{siteId:guid}/download-overage-billing-mode", HandleSetAsync)
            .RequireAuthorization("RequirePlatformOwner");
    }

    private static async Task<IResult> HandleSetAsync(
        Guid siteId,
        SetDownloadOverageBillingModeRequest request,
        SetDownloadOverageBillingModeAsOwnerHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var performedBy = SubjectOf(httpContext);
        if (performedBy is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Token carries no subject claim.");
        }

        // The mode arrives as its own member name, not an ordinal - the same "a client sends and reads a
        // name" convention every other enum on this codebase's wire uses. An unparseable value is the
        // caller's own mistake, answered as a 400 here rather than defaulting to either mode, because
        // defaulting silently would mean a typo in an owner's request quietly picking a billing
        // arrangement for somebody else's account.
        if (!Enum.TryParse<DownloadOverageBillingMode>(request.Mode, ignoreCase: false, out var mode))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: $"'{request.Mode}' is not a download-overage billing mode - expected "
                    + $"'{nameof(DownloadOverageBillingMode.Manual)}' or '{nameof(DownloadOverageBillingMode.AutoBill)}'.");
        }

        var result = await handler.HandleAsync(
            new SetDownloadOverageBillingModeAsOwner(new SiteId(siteId), mode, performedBy, request.Reason),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok();
    }

    /// <summary>The identical "read directly off the validated token, not threaded through" shape
    /// <c>OwnerDownloadBlockExemptionEndpoints.SubjectOf</c>'s own remarks give - recorded, never
    /// authorising; <c>RequirePlatformOwner</c> on the route already decided that.</summary>
    private static string? SubjectOf(HttpContext httpContext) =>
        httpContext.User.FindFirstValue(JwtRegisteredClaimNames.Sub) is { Length: > 0 } sub ? sub : null;

    public sealed record SetDownloadOverageBillingModeRequest(string Mode, string Reason);
}
