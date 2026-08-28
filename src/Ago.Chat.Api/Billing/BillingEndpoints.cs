using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.CreateCheckoutSession;
using Ago.Chat.Application.UseCases.ProcessYooKassaWebhook;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.YooKassa;
using Microsoft.AspNetCore.Http.Extensions;

namespace Ago.Chat.Api.Billing;

/// <summary>
/// `13-02`: the checkout-session creation call (operator-authenticated, the same
/// `"RequireOperatorIdentity"` policy `WebhookEndpoints`/`ConversationsEndpoints`' admin-only routes
/// already use) and the ЮKassa webhook receiver, side by side in one file because they are two halves
/// of one payment lifecycle even though they carry completely different auth shapes.
///
/// <para><b>The webhook route lives in <c>Ago.Chat.Api</c>, not <c>Ago.Chat.Webhooks</c> - stated
/// explicitly because this item's own backlog note asks for the contrast.</b> `adr/0013`'s three-host
/// split isolates <i>our own</i> slow outbound calls to a shop's tenants from the rest of the system
/// ("expected to be slow and failing; must not affect the others"). This is the opposite shape: an
/// <i>inbound</i> request where ЮKassa is the one with latency to manage on its own side, and this
/// system's only obligation is to ack fast and never block - exactly what an ordinary `Ago.Chat.Api`
/// endpoint already does for every other inbound request (the identical reasoning
/// `MaxWebhookEndpoints`'s own remarks give for the same placement decision on MAX's inbound
/// webhook).</para>
/// </summary>
public static class BillingEndpoints
{
    public const string YooKassaSignatureHeaderName = "Webhook-Signature";

    public static void MapBillingEndpoints(this WebApplication app)
    {
        app.MapCreateCheckoutSessionEndpoint();
        app.MapYooKassaWebhookEndpoint();
    }

    /// <summary>Split out from <see cref="MapBillingEndpoints"/> as its own public extension method -
    /// not the usual "one `MapXEndpoints` per feature" shape every other Endpoints class in this
    /// codebase uses, deliberately, because the two routes below have nothing in common but the
    /// feature name: this one needs `RequireOperatorIdentity` and the operator-side handler graph, the
    /// other needs neither. `YooKassaWebhookEndpointTests` (Integration.Tests) maps only
    /// <see cref="MapYooKassaWebhookEndpoint"/> against a real Postgres-backed DI container with no
    /// auth scheme configured at all - splitting the two apart is what makes that test host buildable
    /// without also standing up JWT bearer auth it has no use for.</summary>
    public static void MapCreateCheckoutSessionEndpoint(this WebApplication app) =>
        app.MapPost("/api/v1/sites/{siteId:guid}/billing/checkout-sessions", HandleCreateCheckoutSessionAsync)
            .RequireAuthorization("RequireOperatorIdentity");

    public static void MapYooKassaWebhookEndpoint(this WebApplication app) =>
        app.MapPost("/api/v1/billing/webhooks/yookassa", HandleYooKassaWebhookAsync);

    private static async Task<IResult> HandleCreateCheckoutSessionAsync(
        Guid siteId,
        CreateCheckoutSessionRequest request,
        CreateCheckoutSessionHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new CreateCheckoutSession(user.GetOperatorId(), new SiteId(siteId), request.RequestedSeats), cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(result.Value);
    }

    /// <summary>
    /// Verify (this method's own pre-check, before <see cref="ProcessYooKassaWebhookHandler"/> is even
    /// constructed - the same "endpoint verifies auth, Application handler orchestrates the use case"
    /// split `MaxWebhookEndpoints.HandleAsync` already establishes for MAX's own secret-header check) -
    /// idempotency ledger - terminal state, in that order, matching this item's own backlog. The raw
    /// body is read as text <i>before</i> any JSON parsing, and that same raw string is what the
    /// signature is verified against - reserializing a parsed object would almost certainly change the
    /// byte sequence ЮKassa actually signed (`IYooKassaWebhookSignatureVerifier`'s own remarks).
    /// </summary>
    private static async Task<IResult> HandleYooKassaWebhookAsync(
        HttpContext httpContext,
        IYooKassaWebhookSignatureVerifier verifier,
        ProcessYooKassaWebhookHandler handler,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(httpContext.Request.Body);
        var rawBody = await reader.ReadToEndAsync(cancellationToken);

        var signatureHeader = httpContext.Request.Headers[YooKassaSignatureHeaderName].ToString();
        // GetEncodedUrl() reconstructs the request's own absolute URL from scheme+host+path+query -
        // matching what ЮKassa itself targeted requires this host's ForwardedHeadersMiddleware to have
        // already resolved the external scheme/host from the gateway's forwarded headers
        // (`ForwardedHeadersTests`' own precedent), which Program.cs already configures for every other
        // reason (rate-limit bucket resolution). Not confirmed against a real ЮKassa signature - see
        // YooKassaWebhookSignatureVerifier's own remarks.
        var requestUrl = httpContext.Request.GetEncodedUrl();

        if (!verifier.Verify(httpContext.Request.Method, requestUrl, rawBody, signatureHeader))
        {
            return Results.Unauthorized();
        }

        var parsed = YooKassaWebhookParser.TryParse(rawBody);
        if (parsed is null)
        {
            // Signed by a real ЮKassa webhook key but not a shape this item understands - acked 200
            // rather than rejected, the same "do not burn the provider's retry budget on something that
            // will never parse differently" reasoning MaxWebhookEndpoints' own remarks give.
            return Results.Ok();
        }

        await handler.HandleAsync(
            new ProcessYooKassaWebhook(parsed.YooKassaPaymentId, parsed.EventType, parsed.PaymentMethodId), cancellationToken);

        return Results.Ok();
    }

    public sealed record CreateCheckoutSessionRequest(int RequestedSeats);
}
