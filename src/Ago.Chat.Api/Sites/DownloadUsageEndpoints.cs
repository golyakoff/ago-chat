using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.GetDownloadUsageForSite;
using Ago.Chat.Application.UseCases.PurchaseDownloadOverage;
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

        // `25-84`: the manual path's own checkout. A POST beside the GET rather than in
        // `BillingEndpoints` - this is the download-usage screen's own action, gated by the same
        // `site:configure` permission `BillingEndpoints` uses, and grouping it with the fact it acts on
        // is what keeps a reader from having to know that "pay for downloads" lives under "billing".
        app.MapPost("/api/v1/sites/{siteId:guid}/download-overage/checkout-sessions", HandlePurchaseAsync)
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
            status.IsSoftCrossed, status.IsHardCrossed, status.IsExempt,
            status.BillingMode, status.OutstandingOverageBytes, status.OutstandingOverageRub,
            status.OverageSettledRub, status.AutoBillCapRub, status.IsAtAutoBillCap));
    }

    /// <summary>`25-84`: starts a real ЮKassa checkout for whatever download overage this tenant owes
    /// right now. Answers the confirmation URL, never an unblocked account - only the webhook does that
    /// (<c>PurchaseDownloadOverageHandler</c>'s own remarks).</summary>
    private static async Task<IResult> HandlePurchaseAsync(
        Guid siteId, PurchaseDownloadOverageHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new PurchaseDownloadOverage(httpContext.User.GetOperatorId(), new SiteId(siteId)), cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(result.Value);
    }

    /// <summary>`25-84`: six fields added, all additive - `api-design.md`'s "add within a version, never
    /// remove or rename" (the six original fields keep their original names and positions).</summary>
    public sealed record DownloadUsageResponse(
        long BytesOut, long SoftThresholdBytes, long HardThresholdBytes,
        bool IsSoftCrossed, bool IsHardCrossed, bool IsExempt,
        string BillingMode, long OutstandingOverageBytes, decimal? OutstandingOverageRub,
        decimal OverageSettledRub, decimal? AutoBillCapRub, bool IsAtAutoBillCap);
}
