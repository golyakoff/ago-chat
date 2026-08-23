using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases;
using Ago.Chat.Application.UseCases.GetWebhookDeliveries;
using Ago.Chat.Application.UseCases.ListWebhookEndpoints;
using Ago.Chat.Application.UseCases.RegisterWebhookEndpoint;
using Ago.Chat.Application.UseCases.RevokeWebhookEndpoint;
using Ago.Chat.Domain;

namespace Ago.Chat.Api.Webhooks;

/// <summary>
/// `6-03`: the backend a future self-service console screen calls, built now with no UI in front of
/// it (this item's own Goal). Operator-only throughout - unlike `AttachmentEndpoints`, a visitor never
/// holds `webhook:manage` (`adr/0016`: "Visitor stays outside the role system"), so there is no
/// dual-scheme ambiguity to resolve the way `ClaimsPrincipalExtensions.IsOperator` resolves it there;
/// every route here reuses the same `"RequireOperatorIdentity"` policy `ConversationsEndpoints`'
/// admin-only routes already apply.
/// </summary>
public static class WebhookEndpoints
{
    public static void MapWebhookEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/sites/{siteId:guid}/webhooks")
            .RequireAuthorization("RequireOperatorIdentity");

        group.MapPost("", HandleRegisterAsync);
        group.MapGet("", HandleListAsync);
        group.MapDelete("/{webhookId:guid}", HandleRevokeAsync);
        group.MapGet("/{webhookId:guid}/deliveries", HandleGetDeliveriesAsync);
    }

    private static async Task<IResult> HandleRegisterAsync(
        Guid siteId,
        RegisterWebhookEndpointRequest request,
        RegisterWebhookEndpointHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;

        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var url))
        {
            return ConversationErrors.WebhookInvalidUrl("Webhook endpoint URL must be an absolute URL.").ToProblem(httpContext);
        }

        var result = await handler.HandleAsync(
            new RegisterWebhookEndpoint(user.GetOperatorId(), new SiteId(siteId), url), cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        return Results.Created(
            $"/api/v1/sites/{siteId}/webhooks/{result.Value.WebhookEndpointId}",
            new RegisterWebhookEndpointResponse(
                result.Value.WebhookEndpointId, result.Value.Secret, result.Value.Url.ToString(), result.Value.CreatedAt));
    }

    private static async Task<IResult> HandleListAsync(
        Guid siteId, ListWebhookEndpointsHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new ListWebhookEndpoints(user.GetOperatorId(), new SiteId(siteId)), cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(result.Value);
    }

    private static async Task<IResult> HandleRevokeAsync(
        Guid siteId, Guid webhookId, RevokeWebhookEndpointHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new RevokeWebhookEndpoint(new WebhookEndpointId(webhookId), user.GetOperatorId(), new SiteId(siteId)),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.NoContent();
    }

    private static async Task<IResult> HandleGetDeliveriesAsync(
        Guid siteId,
        Guid webhookId,
        Guid? beforeId,
        int? pageSize,
        GetWebhookDeliveriesHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new GetWebhookDeliveries(
                new WebhookEndpointId(webhookId), user.GetOperatorId(), new SiteId(siteId), beforeId, pageSize ?? 50),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(result.Value);
    }

    public sealed record RegisterWebhookEndpointRequest(string Url);

    /// <summary><see cref="Secret"/> is the plaintext value, present in this response body only - see
    /// `RegisteredWebhookEndpoint`'s own remarks.</summary>
    public sealed record RegisterWebhookEndpointResponse(Guid WebhookEndpointId, string Secret, string Url, DateTimeOffset CreatedAt);
}
