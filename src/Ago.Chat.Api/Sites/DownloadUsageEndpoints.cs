using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.GetDownloadUsageForSite;
using Ago.Chat.Domain;

namespace Ago.Chat.Api.Sites;

/// <summary>
/// `25-83`: `GET /api/v1/sites/{siteId}/download-usage` - the tenant's own read of its own account's
/// download usage, driving the console's own non-dismissable warning banner. Its own file, not folded
/// into <see cref="SiteSuspensionEndpoints"/> or <see cref="SitesEndpoints"/> - the identical "one
/// composable <c>Map...Endpoints</c> extension per concern" precedent that file's own remarks state
/// for itself, restated for a third, unrelated site-scoped read.
/// </summary>
public static class DownloadUsageEndpoints
{
    public static void MapDownloadUsageEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/sites/{siteId:guid}/download-usage", HandleGetAsync)
            .RequireAuthorization("RequireOperatorIdentity");
    }

    private static async Task<IResult> HandleGetAsync(
        Guid siteId, GetDownloadUsageForSiteHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new GetDownloadUsageForSite(httpContext.User.GetOperatorId(), new SiteId(siteId)), cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        var status = result.Value;
        return Results.Ok(new DownloadUsageResponse(
            status.BytesOut, status.SoftThresholdBytes, status.HardThresholdBytes,
            status.IsSoftCrossed, status.IsHardCrossed, status.IsExempt));
    }

    public sealed record DownloadUsageResponse(
        long BytesOut, long SoftThresholdBytes, long HardThresholdBytes,
        bool IsSoftCrossed, bool IsHardCrossed, bool IsExempt);
}
