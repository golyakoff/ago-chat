using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.GetSiteBranding;
using Ago.Chat.Application.UseCases.SubmitLogoUpload;
using Ago.Chat.Application.UseCases.UpdateSiteBranding;
using Ago.Chat.Domain;

namespace Ago.Chat.Api.Branding;

/// <summary>
/// `25-160`: `GET`/`PUT /api/v1/sites/{siteId}/branding` and `POST /api/v1/sites/{siteId}/branding/logo` -
/// the console's new "Почта @" screen's own backend, the same route shape and
/// <c>"RequireOperatorIdentity"</c>/route-level <c>SiteId</c> pattern <c>WidgetConfigEndpoints</c>
/// already establishes for a site-scoped, operator-only admin resource.
///
/// <para><b>The logo upload takes a raw body, not <c>IFormFile</c>/<c>multipart/form-data</c>.</b> This
/// item's own Scope names the endpoint's job as "writes the bytes... via <c>IFileStorage</c>" - a plain
/// file, no other form fields ride alongside it, so there is nothing a multipart envelope would buy over
/// posting the bytes directly with <c>Content-Type</c> naming the image format, the same shape every
/// webhook receiver in this host already reads a raw body with (<c>EmailWebhookEndpoints</c>' own
/// precedent, for a different reason - signature verification - but the same mechanism).</para>
/// </summary>
public static class SiteBrandingEndpoints
{
    public static void MapSiteBrandingEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/sites/{siteId:guid}/branding")
            .RequireAuthorization("RequireOperatorIdentity");

        group.MapGet("", HandleGetAsync);
        group.MapPut("", HandlePutAsync);
        group.MapPost("/logo", HandlePostLogoAsync);
    }

    private static async Task<IResult> HandleGetAsync(
        Guid siteId, GetSiteBrandingHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new GetSiteBranding(new SiteId(siteId), httpContext.User.GetOperatorId()), cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(ToResponse(result.Value));
    }

    private static async Task<IResult> HandlePutAsync(
        Guid siteId,
        UpdateSiteBrandingRequest request,
        UpdateSiteBrandingHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new UpdateSiteBranding(new SiteId(siteId), httpContext.User.GetOperatorId(), request.BrandCompanyName),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(new UpdateSiteBrandingResponse(result.Value));
    }

    private static async Task<IResult> HandlePostLogoAsync(
        Guid siteId, SubmitLogoUploadHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var contentType = httpContext.Request.ContentType ?? string.Empty;

        using var buffer = new MemoryStream();
        await httpContext.Request.Body.CopyToAsync(buffer, cancellationToken);

        var result = await handler.HandleAsync(
            new SubmitLogoUpload(new SiteId(siteId), httpContext.User.GetOperatorId(), contentType, buffer.ToArray()),
            cancellationToken);

        // `ago-root#353`'s own convention gives every other *.RateLimited code its own conservative
        // Retry-After, computed at the HTTP layer from configuration the endpoint already holds. This
        // one is left without it deliberately: five uploads a day is a bucket nobody sits and retries
        // against, unlike a message-send or a visitor-session mint, so a missing header costs this
        // refusal nothing real - the same "some *.RateLimited codes carry no Retry-After" precedent
        // `PhoneVerification.LockedOut` already establishes for itself in `ErrorExtensions`.
        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(new LogoUploadResponse(result.Value.Status.ToString()));
    }

    private static SiteBrandingResponse ToResponse(SiteBrandingDto dto) =>
        new(dto.BrandCompanyName, dto.LogoUrl, dto.LogoStatus.ToString(), dto.LogoRejectionReason);

    public sealed record UpdateSiteBrandingRequest(string? BrandCompanyName);

    public sealed record UpdateSiteBrandingResponse(string? BrandCompanyName);

    public sealed record SiteBrandingResponse(string? BrandCompanyName, string? LogoUrl, string LogoStatus, string? LogoRejectionReason);

    public sealed record LogoUploadResponse(string LogoStatus);
}
