using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.AiAddOn;
using Ago.Chat.Domain;

namespace Ago.Chat.Api.AiAddOn;

/// <summary>
/// `25-04`: `/api/v1/sites/{siteId}/ai-add-on` - the tenant's own surface for the AI add-on. Same route
/// shape, same `"RequireOperatorIdentity"` policy and same "permission is checked in Application, never
/// here" split (`adr/0016`) that <c>OfflineAutoReplyEndpoints</c> established for a site-scoped
/// administrative resource.
///
/// <para><b>Four routes, not one `PUT` with a body of flags - and this is the item's own hardest
/// requirement expressed at the HTTP boundary.</b> Accepting the agreement, declaring a lawful basis and
/// switching the feature on are three separate acts by three separate legal mechanisms (decision 5), and
/// a single `PUT {accepted: true, declared: true, enabled: true}` would let a client make all three at
/// once, from one click, with one timestamp - destroying exactly the distinction the schema keeps. Three
/// endpoints means three requests, three records and three timestamps, and it means a console *cannot*
/// accidentally collapse them.</para>
///
/// <para><b>Nothing here is the vendor call.</b> These routes only write the facts that let the gate say
/// yes later; the two AI paths themselves (<c>ReplyDraftEndpoints</c>, and the worker's own
/// categorisation sweep) are unchanged in shape and simply ask <c>AiProcessingGate</c> first.</para>
/// </summary>
public static class AiAddOnEndpoints
{
    public static void MapAiAddOnEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/sites/{siteId:guid}/ai-add-on")
            .RequireAuthorization("RequireOperatorIdentity");

        group.MapGet("", HandleGetStatusAsync);
        group.MapPost("/acceptance", HandleAcceptAsync);
        group.MapPost("/declaration", HandleDeclareAsync);
        group.MapPost("/enable", HandleEnableAsync);
        group.MapPost("/disable", HandleDisableAsync);
    }

    private static async Task<IResult> HandleGetStatusAsync(
        Guid siteId, GetAiAddOnStatusHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new GetAiAddOnStatus(new SiteId(siteId), httpContext.User.GetOperatorId()), cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        var s = result.Value;
        return Results.Ok(new AiAddOnStatusResponse(
            s.Purchased, s.Enabled, s.EffectiveFrom, s.DocumentKey, s.CurrentVersion, s.CurrentTitle, s.CurrentBody,
            s.AcceptedVersion, s.AcceptedAt, s.DeclaredBy, s.DeclaredAt));
    }

    private static async Task<IResult> HandleAcceptAsync(
        Guid siteId,
        AcceptAgreementRequest request,
        AcceptAiAddOnAgreementHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new AcceptAiAddOnAgreement(
                new SiteId(siteId), httpContext.User.GetOperatorId(), request.Version ?? string.Empty,
                ClientIp(httpContext), UserAgent(httpContext)),
            cancellationToken);

        return result.IsFailure
            ? result.Error!.Value.ToProblem(httpContext)
            : Results.Ok(new AcceptedAgreementResponse(result.Value.DocumentVersion, result.Value.AcceptedAt));
    }

    private static async Task<IResult> HandleDeclareAsync(
        Guid siteId, DeclareAiProcessingBasisHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new DeclareAiProcessingBasis(
                new SiteId(siteId), httpContext.User.GetOperatorId(), ClientIp(httpContext), UserAgent(httpContext)),
            cancellationToken);

        return result.IsFailure
            ? result.Error!.Value.ToProblem(httpContext)
            : Results.Ok(new DeclarationResponse(result.Value.DeclaredBy, result.Value.DeclaredAt));
    }

    private static async Task<IResult> HandleEnableAsync(
        Guid siteId, EnableAiAddOnHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new EnableAiAddOn(new SiteId(siteId), httpContext.User.GetOperatorId()), cancellationToken);

        return result.IsFailure
            ? result.Error!.Value.ToProblem(httpContext)
            : Results.Ok(new EnabledResponse(
                result.Value.EffectiveFrom, result.Value.AcceptedDocumentKey, result.Value.AcceptedDocumentVersion));
    }

    private static async Task<IResult> HandleDisableAsync(
        Guid siteId, DisableAiAddOnHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new DisableAiAddOn(new SiteId(siteId), httpContext.User.GetOperatorId()), cancellationToken);

        return result.IsFailure
            ? result.Error!.Value.ToProblem(httpContext)
            : Results.Ok(new DisabledResponse(result.Value.DisabledAt));
    }

    /// <summary>Best-effort, the same fallback <c>DocumentEndpoints</c> uses - and truncated to the
    /// column's own bound rather than letting a long forged header fail the write, since the record is
    /// worth more than the header is.</summary>
    private static string? ClientIp(HttpContext httpContext)
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString();
        return ip is null ? null : Truncate(ip, AiProcessingBasisDeclaration.MaxClientIpLength);
    }

    private static string? UserAgent(HttpContext httpContext)
    {
        var agent = httpContext.Request.Headers.UserAgent.ToString();
        return string.IsNullOrWhiteSpace(agent) ? null : Truncate(agent, AiProcessingBasisDeclaration.MaxUserAgentLength);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    /// <summary>The version the caller says they read - see <c>AcceptAiAddOnAgreementHandler</c> for why
    /// it is checked against the published current version rather than trusted.</summary>
    public sealed record AcceptAgreementRequest(string? Version);

    public sealed record AcceptedAgreementResponse(string Version, DateTimeOffset AcceptedAt);

    public sealed record DeclarationResponse(Guid DeclaredBy, DateTimeOffset DeclaredAt);

    public sealed record EnabledResponse(
        DateTimeOffset EffectiveFrom, string AcceptedDocumentKey, string AcceptedDocumentVersion);

    public sealed record DisabledResponse(DateTimeOffset? DisabledAt);

    /// <summary>The wire shape - the two evidence pairs stay apart here exactly as they do in the
    /// schema, so a console cannot render "accepted and declared" as one checkbox.</summary>
    public sealed record AiAddOnStatusResponse(
        bool Purchased,
        bool Enabled,
        DateTimeOffset? EffectiveFrom,
        string DocumentKey,
        string? CurrentVersion,
        string? CurrentTitle,
        string? CurrentBody,
        string? AcceptedVersion,
        DateTimeOffset? AcceptedAt,
        Guid? DeclaredBy,
        DateTimeOffset? DeclaredAt);
}
