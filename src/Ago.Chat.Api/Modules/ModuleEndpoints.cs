using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.EnableModuleForSite;
using Ago.Chat.Application.UseCases.RevokeModuleForSite;
using Ago.Chat.Application.UseCases.RotateModuleCredential;
using Ago.Chat.Application.UseCases.VerifyModuleRegistration;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;
using Microsoft.AspNetCore.Mvc;

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

        // `22-11`: the lifecycle PUT alone did not have - see EnableModuleForSiteHandler's own
        // remarks on why a create-only registry left a leaked credential with no remedy and a site's
        // access with no way to end.
        group.MapPost("/{moduleKey}/rotate", HandleRotateAsync);
        group.MapDelete("/{moduleKey}", HandleRevokeAsync);
        group.MapPost("/{moduleKey}/verify", HandleVerifyAsync);
    }

    private static async Task<IResult> HandleGetAsync(
        Guid siteId, IEnabledModuleReadStore readStore, IClock clock, CancellationToken cancellationToken)
    {
        var modules = await readStore.GetForSiteAsync(new SiteId(siteId), clock.UtcNow, cancellationToken);
        return Results.Ok(new EnabledModulesResponse(
            [.. modules.Select(m => new EnableModuleResponse(
                m.ModuleKey.Value, m.TriggerWords, m.EntryPoint.ToString(), m.GrantedByOwner, m.ExpiresAt))]));
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
                request.Credential, request.ProvisioningSecret),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(new EnableModuleResponse(
            request.ModuleKey, request.TriggerWords, request.EntryPoint));
    }

    /// <summary>`22-11`: mints a fresh credential and installs it on both sides - see
    /// <see cref="RotateModuleCredentialHandler"/>'s own remarks for why this handler mints rather than
    /// accepts one, unlike <see cref="HandlePutAsync"/>.</summary>
    private static async Task<IResult> HandleRotateAsync(
        Guid siteId,
        string moduleKey,
        RotateModuleCredentialRequest request,
        RotateModuleCredentialHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new RotateModuleCredential(httpContext.User.GetOperatorId(), new SiteId(siteId), moduleKey, request.ProvisioningSecret),
            cancellationToken);

        // `22-11`: the one place a credential this codebase mints is ever echoed back - the operator
        // has no other way to learn a value Chat generated on its own behalf, the same "shown once"
        // hygiene IWebhookSecretGenerator's own remarks describe for its sibling.
        return result.IsFailure
            ? result.Error!.Value.ToProblem(httpContext)
            : Results.Ok(new RotateModuleCredentialResponse(result.Value.NewCredential.Value));
    }

    private static async Task<IResult> HandleRevokeAsync(
        Guid siteId,
        string moduleKey,
        // `DELETE` does not allow an inferred body parameter (Minimal API's own RequestDelegateFactory
        // refuses to infer one for GET/HEAD/DELETE) - found by this item's own integration test, not
        // by inspection, the same "found running, not reasoned about" shape this project's own
        // fails-before discipline keeps surfacing.
        [FromBody] RevokeModuleRequest request,
        RevokeModuleForSiteHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new RevokeModuleForSite(httpContext.User.GetOperatorId(), new SiteId(siteId), moduleKey, request.ProvisioningSecret),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok();
    }

    /// <summary>`22-11`'s own fourth Done-when's operator-facing surface - see
    /// <see cref="VerifyModuleRegistrationHandler"/>'s own remarks for what this can and cannot
    /// prove.</summary>
    private static async Task<IResult> HandleVerifyAsync(
        Guid siteId,
        string moduleKey,
        VerifyModuleRegistrationRequest request,
        VerifyModuleRegistrationHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new VerifyModuleRegistration(
                httpContext.User.GetOperatorId(), new SiteId(siteId), moduleKey, request.EntryPoint, request.ProvisioningSecret),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(new VerifyModuleRegistrationResponse(
            result.Value.ChatHasRegistration, result.Value.ModuleHasRegistration, result.Value.Agree));
    }

    /// <param name="Credential">`22-02`: the shared secret this site's module calls will be signed
    /// with - never echoed back in <see cref="EnableModuleResponse"/> once written, the same
    /// "a secret is accepted, never returned" hygiene a password field would get.</param>
    /// <param name="ProvisioningSecret">`22-11`: proves this call may provision on the module
    /// deployment's own behalf - see <see cref="Domain.ModuleProvisioningSecret"/>'s own remarks.</param>
    public sealed record EnableModuleRequest(
        string ModuleKey, IReadOnlyList<string> TriggerWords, string EntryPoint, string Credential,
        string ProvisioningSecret);

    /// <param name="GrantedByOwner">`22-17`: <see langword="true"/> when the platform owner enabled this
    /// module rather than the tenant's own operator - the wire-visible half of this item's own audit
    /// distinction. Always <see langword="false"/> on <see cref="HandlePutAsync"/>'s own response,
    /// which only ever writes a self-service grant.</param>
    /// <param name="ExpiresAt"><see langword="null"/> for a grant that does not expire.</param>
    public sealed record EnableModuleResponse(
        string ModuleKey, IReadOnlyList<string> TriggerWords, string EntryPoint, bool GrantedByOwner = false,
        DateTimeOffset? ExpiresAt = null);

    public sealed record EnabledModulesResponse(IReadOnlyList<EnableModuleResponse> Modules);

    public sealed record RotateModuleCredentialRequest(string ProvisioningSecret);

    public sealed record RotateModuleCredentialResponse(string NewCredential);

    public sealed record RevokeModuleRequest(string ProvisioningSecret);

    public sealed record VerifyModuleRegistrationRequest(string EntryPoint, string ProvisioningSecret);

    public sealed record VerifyModuleRegistrationResponse(bool ChatHasRegistration, bool ModuleHasRegistration, bool Agree);
}
