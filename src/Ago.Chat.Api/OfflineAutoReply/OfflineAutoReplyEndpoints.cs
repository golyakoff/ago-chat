using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.GetOfflineAutoReply;
using Ago.Chat.Application.UseCases.UpdateOfflineAutoReply;
using Ago.Chat.Domain;

namespace Ago.Chat.Api.OfflineAutoReply;

/// <summary>
/// `14-04`: `GET`/`PUT /api/v1/sites/{siteId}/offline-auto-reply` - the same route shape and the same
/// `"RequireOperatorIdentity"` policy `11-01`'s `WidgetConfigEndpoints` established for a site-scoped,
/// operator-only admin resource, and the same `site:configure` permission behind it (checked in
/// Application, never here - `adr/0016`). `siteId` comes from the route rather than
/// <c>user.GetSiteId()</c>, for the reason that file already states: an operator's own site claim is
/// not necessarily the site being configured.
///
/// <para>The wire shape is flat <c>{keyword, reply}</c> objects, deliberately not the Domain
/// <c>OfflineAutoReplyRule</c> struct serialised directly - a value object's constructor throwing
/// during model binding would surface as a 400 with an unusable message, and the whole point of
/// carrying raw strings into <c>UpdateOfflineAutoReplyHandler</c> is that it can turn a bad rule into
/// a real problem+json error with a stable <c>type</c> code.</para>
/// </summary>
public static class OfflineAutoReplyEndpoints
{
    public static void MapOfflineAutoReplyEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/sites/{siteId:guid}/offline-auto-reply")
            .RequireAuthorization("RequireOperatorIdentity");

        group.MapGet("", HandleGetAsync);
        group.MapPut("", HandlePutAsync);
    }

    private static async Task<IResult> HandleGetAsync(
        Guid siteId, GetOfflineAutoReplyHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new GetOfflineAutoReply(new SiteId(siteId), httpContext.User.GetOperatorId()), cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(ToResponse(result.Value));
    }

    private static async Task<IResult> HandlePutAsync(
        Guid siteId,
        OfflineAutoReplyRequest request,
        UpdateOfflineAutoReplyHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var rules = (request.Rules ?? [])
            .Select(rule => new UpdateOfflineAutoReplyRule(rule.Keyword ?? string.Empty, rule.Reply ?? string.Empty))
            .ToList();

        var result = await handler.HandleAsync(
            new UpdateOfflineAutoReply(
                new SiteId(siteId), httpContext.User.GetOperatorId(),
                request.Enabled, request.FallbackReply ?? string.Empty, rules),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(ToResponse(result.Value));
    }

    private static OfflineAutoReplyResponse ToResponse(OfflineAutoReplySettings settings) =>
        new(settings.Enabled, settings.FallbackReply,
            [.. settings.Rules.Select(r => new OfflineAutoReplyRuleDto(r.Keyword, r.Reply))]);

    /// <summary>Every string is nullable on the request only because a client can omit it; the handler
    /// is what decides that an omitted fallback on an enabled configuration is an error, and says so
    /// with a code (api-design.md).</summary>
    public sealed record OfflineAutoReplyRequest(
        bool Enabled, string? FallbackReply, IReadOnlyList<OfflineAutoReplyRuleDto>? Rules);

    public sealed record OfflineAutoReplyResponse(
        bool Enabled, string FallbackReply, IReadOnlyList<OfflineAutoReplyRuleDto> Rules);

    public sealed record OfflineAutoReplyRuleDto(string? Keyword, string? Reply);
}
