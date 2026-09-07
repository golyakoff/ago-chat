using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.GetSiteConsentAcceptances;
using Ago.Chat.Application.UseCases.GetSiteConsentDocuments;
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
///
/// <para><b>`23-37`: two `GET`s join the `POST` above</b> - the console's own read side for the same
/// surface (list every version of both purposes; list who accepted a given purpose). Same file, same
/// `"RequireOperatorIdentity"` route gate, same "the real check is inside the handler" shape - see
/// `GetSiteConsentDocumentsHandler`/`GetSiteConsentAcceptancesHandler`'s own remarks for why
/// `Permission.SiteConfigure`, checked against the route's own `siteId`, is the entire isolation story
/// for both.</para>
/// </summary>
public static class SiteConsentDocumentEndpoints
{
    public static void MapSiteConsentDocumentEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/sites/{siteId:guid}/consent-documents/{purpose}", HandlePublishAsync)
            .RequireAuthorization("RequireOperatorIdentity");

        app.MapGet("/api/v1/sites/{siteId:guid}/consent-documents", HandleListAsync)
            .RequireAuthorization("RequireOperatorIdentity");

        app.MapGet("/api/v1/sites/{siteId:guid}/consent-documents/{purpose}/acceptances", HandleAcceptancesAsync)
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

    private static async Task<IResult> HandleListAsync(
        Guid siteId, GetSiteConsentDocumentsHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new GetSiteConsentDocuments(new SiteId(siteId), user.GetOperatorId()), cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        var overview = result.Value;
        return Results.Ok(new SiteConsentDocumentsResponse(
            ToResponse(overview.Contact), overview.ContactConsentRequired, ToResponse(overview.Marketing)));
    }

    private static async Task<IResult> HandleAcceptancesAsync(
        Guid siteId,
        string purpose,
        GetSiteConsentAcceptancesHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new GetSiteConsentAcceptances(new SiteId(siteId), purpose, user.GetOperatorId()), cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        return Results.Ok(result.Value
            .Select(a => new SiteConsentAcceptanceResponse(a.SubjectKind, a.SubjectId, a.DocumentVersion, a.AcceptedAt))
            .ToList());
    }

    private static SiteConsentDocumentResponse ToResponse(SiteConsentDocumentSummary summary) =>
        new(
            summary.Purpose,
            summary.DocumentKey,
            summary.Versions.Select(v => new PublishedVersionResponse(v.Version, v.Sequence, v.Title, v.PublishedAt)).ToList());

    public sealed record PublishSiteConsentDocumentRequest(string? Title, string? Body);

    public sealed record PublishedSiteConsentDocumentResponse(
        string DocumentKey, string Version, int Sequence, string Title, string Body, DateTimeOffset PublishedAt);

    /// <summary>`23-37`'s own wire shape for `GET /api/v1/sites/{siteId}/consent-documents`.
    /// `ContactConsentRequired` sits beside `Contact`, not inside it - see
    /// `SiteConsentDocumentsOverview`'s own remarks for why it is a fact about the site's widget
    /// configuration, not about the document.</summary>
    public sealed record SiteConsentDocumentsResponse(
        SiteConsentDocumentResponse Contact, bool ContactConsentRequired, SiteConsentDocumentResponse Marketing);

    public sealed record SiteConsentDocumentResponse(
        string Purpose, string DocumentKey, IReadOnlyList<PublishedVersionResponse> Versions);

    public sealed record PublishedVersionResponse(string Version, int Sequence, string Title, DateTimeOffset PublishedAt);

    /// <summary>`23-37`'s own wire shape for the acceptances read - deliberately narrower than
    /// `Ago.Chat.Domain.AcceptanceRecord`; see `SiteConsentAcceptanceDto`'s own remarks for why
    /// `ClientIp`/`UserAgent` do not cross this wire.</summary>
    public sealed record SiteConsentAcceptanceResponse(string SubjectKind, Guid SubjectId, string DocumentVersion, DateTimeOffset AcceptedAt);
}
