using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.GetWidgetConfig;
using Ago.Chat.Application.UseCases.UpdateWidgetConfig;
using Ago.Chat.Domain;

namespace Ago.Chat.Api.WidgetConfig;

/// <summary>
/// `11-01`: `GET`/`PUT /api/v1/sites/{siteId}/widget-config` - the same route shape
/// `WebhookEndpoints` already established for a site-scoped, operator-only admin resource (`site:configure`
/// here instead of `webhook:manage`, the identical `"RequireOperatorIdentity"` policy plus a route-level
/// `SiteId`, not `user.GetSiteId()` from the claim - an operator's own site claim is not necessarily
/// the site being configured, the same reason `WebhookEndpoints` reads `siteId` from the route too).
///
/// `Position` crosses the wire as its PascalCase member name (`"BottomRight"`/`"BottomLeft"`), the
/// same `.ToString()` convention `VisitorHub`/`OperatorHub` already use for `AuthorKind` - a
/// data-model-level, kebab-case storage choice (`PositionConverter`'s own remarks) is free to differ
/// from the wire shape, the same way `MessageBodyConverter` and a wire DTO already can.
///
/// `11-10`: `Locale` joins the request/response on the identical terms and crosses the wire the same
/// way - its own PascalCase member name (`"En"`/`"Ru"`), independent of `LocaleConverter`'s lowercase
/// storage choice.
/// </summary>
public static class WidgetConfigEndpoints
{
    public static void MapWidgetConfigEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/sites/{siteId:guid}/widget-config")
            .RequireAuthorization("RequireOperatorIdentity");

        group.MapGet("", HandleGetAsync);
        group.MapPut("", HandlePutAsync);
    }

    private static async Task<IResult> HandleGetAsync(
        Guid siteId, GetWidgetConfigHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new GetWidgetConfig(new SiteId(siteId), user.GetOperatorId()), cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(ToResponse(result.Value));
    }

    private static async Task<IResult> HandlePutAsync(
        Guid siteId,
        UpdateWidgetConfigRequest request,
        UpdateWidgetConfigHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new UpdateWidgetConfig(
                new SiteId(siteId), user.GetOperatorId(), request.PrimaryColorHex, request.Position, request.Locale),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(ToResponse(result.Value));
    }

    private static WidgetConfigResponse ToResponse(Application.UseCases.GetWidgetConfig.WidgetConfigDto dto) =>
        new(dto.PrimaryColorHex, dto.Position.ToString(), dto.Locale.ToString());

    public sealed record UpdateWidgetConfigRequest(string? PrimaryColorHex, string Position, string Locale);

    public sealed record WidgetConfigResponse(string? PrimaryColorHex, string Position, string Locale);
}
