using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.GetWidgetConfig;
using Ago.Chat.Application.UseCases.UpdateWidgetConfig;
using Ago.Chat.Domain;
using System.Text.Json.Serialization;

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
///
/// `16-04`: `NoticeText`/`NoticeUrl` join as two more additive, nullable string fields - no enum
/// conversion needed, they cross the wire exactly as `Ago.Chat.Domain.WidgetConfig` holds them.
///
/// `23-63`: `AttractAttention` joins as a plain bool, off by default - a tenant turns «Привлекать
/// внимание» on here, and it rides `SiteSettingsChanged`/`SiteConfigDto` onto a returning visitor's
/// own bootstrap the same way every other field on this endpoint already does.
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
                new SiteId(siteId),
                user.GetOperatorId(),
                request.PrimaryColorHex,
                request.Position,
                request.Locale,
                request.NoticeText,
                request.NoticeUrl,
                request.RequireContactConsent,
                request.AttractAttention,
                request.AutoOpenEnabled,
                request.AutoOpenDelaySeconds,
                request.AutoOpenGreetingText),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(ToResponse(result.Value));
    }

    private static WidgetConfigResponse ToResponse(Application.UseCases.GetWidgetConfig.WidgetConfigDto dto) =>
        new(dto.PrimaryColorHex, dto.Position.ToString(), dto.Locale.ToString(), dto.NoticeText, dto.NoticeUrl,
            dto.RequireContactConsent, dto.AttractAttention, dto.AutoOpenEnabled, (int)dto.AutoOpenDelaySeconds,
            dto.AutoOpenGreetingText);

    /// <summary>
    /// <para>
    /// `23-108`: <see cref="RequireContactConsent"/> carries <c>[JsonRequired]</c> and the others do
    /// not, and the asymmetry is the point. This is a PUT - a full replacement - so every field is
    /// meant to be sent, but only one of them is a <b>gate</b>: while it is on, no contact detail is
    /// recorded until the visitor has accepted the tenant's consent document
    /// (<c>GetConsentRequirement</c>).
    /// </para>
    /// <para>
    /// A missing <c>bool</c> binds to <c>false</c>, silently, and for a gate that means an omission
    /// turns enforcement off while looking like an ordinary save. `ago-console` did exactly that for
    /// as long as this field existed: its own DTO had no such property and it serialises that object
    /// as the whole body, so saving a colour would have cleared the gate. Nobody noticed because the
    /// console also had no way to switch the gate on, so the value was never anything but
    /// <c>false</c> to begin with.
    /// </para>
    /// <para>
    /// The other fields are left as they are deliberately. A missing colour binding to <c>null</c>
    /// means "no colour", which is a legitimate value a tenant can choose, and the same is true of the
    /// notice text and its URL - there is no state those can silently destroy. Marking every field
    /// required would be tidier and would say something false about what is at stake.
    /// </para>
    /// </summary>
    public sealed record UpdateWidgetConfigRequest(
        string? PrimaryColorHex, string Position, string Locale, string? NoticeText, string? NoticeUrl,
        [property: JsonRequired] bool RequireContactConsent, bool AttractAttention,
        bool AutoOpenEnabled = false, int AutoOpenDelaySeconds = 30, string? AutoOpenGreetingText = null);

    /// <summary>`23-64`: <c>AutoOpenDelaySeconds</c> crosses the wire as its plain `int` value
    /// (`AutoOpenDelay`'s own remarks on why it needs no PascalCase-string convention the way
    /// `Position`/`Locale` do) - `(int)dto.AutoOpenDelaySeconds` at the one call site that builds this
    /// response, not a `.ToString()` the way those two enums use.</summary>
    public sealed record WidgetConfigResponse(
        string? PrimaryColorHex, string Position, string Locale, string? NoticeText, string? NoticeUrl,
        bool RequireContactConsent, bool AttractAttention, bool AutoOpenEnabled, int AutoOpenDelaySeconds,
        string? AutoOpenGreetingText);
}
