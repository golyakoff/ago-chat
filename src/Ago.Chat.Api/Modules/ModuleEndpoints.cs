using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.EnableModuleForSite;
using Ago.Chat.Domain;

namespace Ago.Chat.Api.Modules;

/// <summary>
/// `19-03`: the HTTP surface `20-07` deliberately left unbuilt - <c>EnableModuleForSite</c>'s own doc
/// comment names it as "optional/nice-to-have... ready to sit behind one whenever that endpoint is
/// built." This item needed a real console screen to register the FAQ module for a site, so this is
/// that endpoint - the same <c>"RequireOperatorIdentity"</c> + route-level <c>siteId</c> +
/// <c>Permission.SiteConfigure</c> shape <see cref="WidgetConfig.WidgetConfigEndpoints"/> already
/// established, reused rather than invented (the permission check itself lives inside
/// <see cref="EnableModuleForSiteHandler"/>, not here - this file only translates HTTP to command and
/// command to HTTP, same split every other endpoint file in this folder makes).
///
/// <para><b>Generic across every module, not FAQ-specific.</b> Nothing here names <c>"faq"</c> - the
/// module key is a request body field, exactly like every other <see cref="ModuleKey"/>-typed value
/// this codebase carries as an opaque string. That is what keeps this file on the right side of the
/// guard 2 test (<c>ModuleKeyLiteralRule</c>): a *console screen* is free to know it is registering the
/// FAQ module (it is choosing the request body), but this *endpoint* - like
/// <see cref="EnableModuleForSiteHandler"/> beneath it - never does.</para>
/// </summary>
public static class ModuleEndpoints
{
    public static void MapModuleEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/sites/{siteId:guid}/modules")
            .RequireAuthorization("RequireOperatorIdentity");

        group.MapGet("", HandleGetAsync);
        group.MapPut("", HandlePutAsync);
    }

    private static async Task<IResult> HandleGetAsync(
        Guid siteId, IEnabledModuleReadStore readStore, CancellationToken cancellationToken)
    {
        var modules = await readStore.GetForSiteAsync(new SiteId(siteId), cancellationToken);
        return Results.Ok(new EnabledModulesResponse(
            [.. modules.Select(m => new EnableModuleResponse(m.ModuleKey.Value, m.TriggerWords, m.EntryPoint.ToString()))]));
    }

    private static async Task<IResult> HandlePutAsync(
        Guid siteId,
        EnableModuleRequest request,
        EnableModuleForSiteHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new EnableModuleForSite(
                user.GetOperatorId(), new SiteId(siteId), request.ModuleKey, request.TriggerWords, request.EntryPoint,
                request.Credential),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(new EnableModuleResponse(
            request.ModuleKey, request.TriggerWords, request.EntryPoint));
    }

    /// <param name="Credential">`22-02`: the shared secret this site's module calls will be signed
    /// with - never echoed back in <see cref="EnableModuleResponse"/> once written, the same
    /// "a secret is accepted, never returned" hygiene a password field would get.</param>
    public sealed record EnableModuleRequest(
        string ModuleKey, IReadOnlyList<string> TriggerWords, string EntryPoint, string Credential);

    public sealed record EnableModuleResponse(string ModuleKey, IReadOnlyList<string> TriggerWords, string EntryPoint);

    public sealed record EnabledModulesResponse(IReadOnlyList<EnableModuleResponse> Modules);
}
