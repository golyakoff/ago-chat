using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.PublishDocumentVersion;
using Ago.Chat.Domain;

namespace Ago.Chat.Api.Consent;

/// <summary>
/// `24-05`: `POST /api/v1/sites/{siteId}/consent-documents/{purpose}` - the tenant's own counterpart to
/// `OwnerDocumentEndpoints`'s platform-owner-only publish route, and the mechanism this item's own
/// Goal names: "without AGO authoring the words". Gated by `"RequireOperatorIdentity"` at the route,
/// the same shape `WidgetConfigEndpoints` already uses for this exact site - the real access-control
/// decision (does this operator hold `site:configure` on this site) happens inside
/// `PublishDocumentVersionHandler.HandleAsSiteConsentAsync`, not here.
///
/// <para><b>No `documentKey` anywhere in this file.</b> `purpose` is the only thing the URL names -
/// `SiteConsentDocumentKey.For` (`PublishDocumentVersionHandler`'s own call) derives the actual storage
/// key from `siteId`/`purpose` alone, so there is no request field this endpoint could mis-trust into
/// letting an operator target a document outside their own site.</para>
/// </summary>
public static class SiteConsentDocumentEndpoints
{
    public static void MapSiteConsentDocumentEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/sites/{siteId:guid}/consent-documents/{purpose}", HandlePublishAsync)
            .RequireAuthorization("RequireOperatorIdentity");
    }

    private static async Task<IResult> HandlePublishAsync(
        Guid siteId,
        string purpose,
        PublishSiteConsentDocumentRequest request,
        PublishDocumentVersionHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsSiteConsentAsync(
            new PublishSiteConsentDocumentVersion(
                new SiteId(siteId), user.GetOperatorId(), purpose, request.Title ?? string.Empty, request.Body ?? string.Empty),
            cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        var dto = result.Value;
        return Results.Ok(new PublishedSiteConsentDocumentResponse(dto.DocumentKey, dto.Version, dto.Sequence, dto.Title, dto.Body, dto.PublishedAt));
    }

    public sealed record PublishSiteConsentDocumentRequest(string? Title, string? Body);

    public sealed record PublishedSiteConsentDocumentResponse(
        string DocumentKey, string Version, int Sequence, string Title, string Body, DateTimeOffset PublishedAt);
}
