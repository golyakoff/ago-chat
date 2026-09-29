using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.CancelSubscription;
using Ago.Chat.Application.UseCases.ChangeSubscriptionSeats;
using Ago.Chat.Application.UseCases.PurchaseAdministratorSlot;
using Ago.Chat.Application.UseCases.PurchaseChannelAddOn;
using Ago.Chat.Application.UseCases.CreateCheckoutSession;
using Ago.Chat.Application.UseCases.CreateTokenPayment;
using Ago.Chat.Application.UseCases.GetBillingStatus;
using Ago.Chat.Application.UseCases.PreviewBillingPurchase;
using Ago.Chat.Application.UseCases.ProcessYooKassaWebhook;
using Ago.Chat.Application.UseCases.RenewNow;
using Ago.Chat.Application.UseCases.SetNextPeriodComposition;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.YooKassa;

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
    public static void MapBillingEndpoints(this WebApplication app)
    {
        app.MapCreateCheckoutSessionEndpoint();
        app.MapCreateTokenPaymentEndpoint();
        app.MapYooKassaWebhookEndpoint();
        app.MapCancelSubscriptionEndpoint();
        app.MapChangeSubscriptionSeatsEndpoint();
        app.MapPurchaseAdministratorSlotEndpoint();
        app.MapPurchaseChannelAddOnEndpoint();
        app.MapGetBillingStatusEndpoint();
        app.MapRenewNowEndpoint();
        app.MapSetNextPeriodCompositionEndpoint();
        app.MapPreviewBillingPurchaseEndpoint();
    }

    /// <summary>`26-299`: the console billing-v2 "Card B" write - see
    /// <see cref="Application.UseCases.SetNextPeriodComposition.SetNextPeriodComposition"/>'s own remarks.
    /// Same `RequireOperatorIdentity` + `Permission.SiteConfigure` gate every other billing write in this
    /// file already uses.</summary>
    public static void MapSetNextPeriodCompositionEndpoint(this WebApplication app) =>
        app.MapPost(
            "/api/v1/sites/{siteId:guid}/billing/subscriptions/{subscriptionId:guid}/next-period", HandleSetNextPeriodCompositionAsync)
            .RequireAuthorization("RequireOperatorIdentity");

    /// <summary>`26-299`: a read, not a write - still gated identically to every other billing route in
    /// this file (`Permission.SiteConfigure`, via the handler), since the numbers it answers are exactly
    /// what a billing-decision screen needs and nothing this codebase shows to anyone without that
    /// permission. See <see cref="Application.UseCases.PreviewBillingPurchase.PreviewBillingPurchase"/>'s
    /// own remarks for why a `POST`, not a `GET`, despite being read-only: the query shape (a discriminated
    /// kind plus whichever one field it needs) is naturally a request body, not a clean query string.</summary>
    public static void MapPreviewBillingPurchaseEndpoint(this WebApplication app) =>
        app.MapPost(
            "/api/v1/sites/{siteId:guid}/billing/subscriptions/{subscriptionId:guid}/purchase-preview", HandlePreviewBillingPurchaseAsync)
            .RequireAuthorization("RequireOperatorIdentity");

    /// <summary>`26-291`: the YooKassa Android SDK's own token-payment counterpart to
    /// <see cref="MapCreateCheckoutSessionEndpoint"/> right below - same route family, same
    /// `RequireOperatorIdentity` gate, a `/token` suffix rather than a second top-level resource because
    /// this is the identical use case (create a pending payment, save the subscription) reaching the
    /// same port a second way (`CreateTokenPaymentHandler`'s own remarks).</summary>
    public static void MapCreateTokenPaymentEndpoint(this WebApplication app) =>
        app.MapPost("/api/v1/sites/{siteId:guid}/billing/checkout-sessions/token", HandleCreateTokenPaymentAsync)
            .RequireAuthorization("RequireOperatorIdentity");

    /// <summary>`26-296`: pay-early - the identical `RequireOperatorIdentity` + `Permission.SiteConfigure`
    /// gate (via `RenewNowHandler`) every other billing write in this file already uses.</summary>
    public static void MapRenewNowEndpoint(this WebApplication app) =>
        app.MapPost(
            "/api/v1/sites/{siteId:guid}/billing/subscriptions/{subscriptionId:guid}/renew-now", HandleRenewNowAsync)
            .RequireAuthorization("RequireOperatorIdentity");

    /// <summary>`13-04`: the console billing screen's own bootstrap read - `GetBillingStatus`'s own
    /// remarks on why this did not already exist. Same `Permission.SiteConfigure` gate every other
    /// route in this file uses for a billing decision or a billing view of one.</summary>
    public static void MapGetBillingStatusEndpoint(this WebApplication app) =>
        app.MapGet("/api/v1/sites/{siteId:guid}/billing/status", HandleGetBillingStatusAsync)
            .RequireAuthorization("RequireOperatorIdentity");

    /// <summary>`13-03`: `decisions/0006`'s cancellation - `RequireOperatorIdentity` +
    /// `Permission.SiteConfigure`, the identical gate the checkout endpoint above already uses for a
    /// billing/tier decision.</summary>
    public static void MapCancelSubscriptionEndpoint(this WebApplication app) =>
        app.MapPost(
            "/api/v1/sites/{siteId:guid}/billing/subscriptions/{subscriptionId:guid}/cancel", HandleCancelSubscriptionAsync)
            .RequireAuthorization("RequireOperatorIdentity");

    /// <summary>`13-03`: `decisions/0006`'s mid-cycle seat change - a single endpoint for both an
    /// immediate, charged upgrade and a deferred, uncharged downgrade
    /// (<see cref="ChangeSubscriptionSeatsHandler"/>'s own remarks on why one endpoint, not two).</summary>
    public static void MapChangeSubscriptionSeatsEndpoint(this WebApplication app) =>
        app.MapPost(
            "/api/v1/sites/{siteId:guid}/billing/subscriptions/{subscriptionId:guid}/seats", HandleChangeSubscriptionSeatsAsync)
            .RequireAuthorization("RequireOperatorIdentity");

    /// <summary>`25-41`: the identical seat-purchase shape immediately above, restated for a flat
    /// Administrator-slot add-on - always an immediate, charged increase, never a downgrade branch
    /// (<see cref="PurchaseAdministratorSlot"/>'s own remarks on why there is no self-service
    /// decrease).</summary>
    public static void MapPurchaseAdministratorSlotEndpoint(this WebApplication app) =>
        app.MapPost(
            "/api/v1/sites/{siteId:guid}/billing/subscriptions/{subscriptionId:guid}/administrators", HandlePurchaseAdministratorSlotAsync)
            .RequireAuthorization("RequireOperatorIdentity");

    /// <summary>`26-278`: the identical prorated, charge-then-apply purchase shape immediately above,
    /// restated for a connected-channel add-on - <c>{baseSubscriptionId}</c>, not a bare
    /// <c>{siteId}</c>, names exactly which of the site's own <see cref="Domain.BillingSubscription"/>
    /// rows this purchase charges against and aligns its own new option's period to
    /// (<see cref="PurchaseChannelAddOn"/>'s own remarks).</summary>
    public static void MapPurchaseChannelAddOnEndpoint(this WebApplication app) =>
        app.MapPost(
            "/api/v1/sites/{siteId:guid}/billing/subscriptions/{baseSubscriptionId:guid}/channels", HandlePurchaseChannelAddOnAsync)
            .RequireAuthorization("RequireOperatorIdentity");

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
            new CreateCheckoutSession(user.GetOperatorId(), new SiteId(siteId), request.RequestedSeats, request.SavePaymentMethod),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(result.Value);
    }

    private static async Task<IResult> HandleCreateTokenPaymentAsync(
        Guid siteId,
        CreateTokenPaymentRequest request,
        CreateTokenPaymentHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new CreateTokenPayment(user.GetOperatorId(), new SiteId(siteId), request.RequestedSeats, request.PaymentToken, request.SavePaymentMethod),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(result.Value);
    }

    private static async Task<IResult> HandleRenewNowAsync(
        Guid siteId,
        Guid subscriptionId,
        RenewNowHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new RenewNow(user.GetOperatorId(), new SiteId(siteId), new BillingSubscriptionId(subscriptionId)), cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(result.Value);
    }

    /// <summary>
    /// `26-286`: ЮKassa does not sign its console-configured HTTP notifications (there is no
    /// `Webhook-Signature` header and no shared webhook key - `adr/0071`'s HMAC assumption, made without
    /// network access to confirm it, was wrong and would have rejected every real notification). Its own
    /// documented verification, and what this endpoint now does, is: (1) an IP allowlist check that the
    /// request came from one of ЮKassa's published notification networks
    /// (<see cref="YooKassaWebhookSourceGuard"/>), then (2) hand the notified payment id to
    /// <see cref="ProcessYooKassaWebhookHandler"/>, which re-queries the payment from ЮKassa's own API and
    /// acts on the authoritative status - never on anything this request body claims.
    ///
    /// <para>The re-query is the real guarantee; the IP check is defense-in-depth (see
    /// <see cref="YooKassaWebhookSourceGuard"/>). Nothing in the request body is trusted except the
    /// payment id, and even that only as a lookup key the re-query then validates against ЮKassa itself.
    /// Everything short of a source-IP rejection acks `200` - a body that does not parse, an unknown
    /// payment id, a non-terminal status - so ЮKassa does not retry something that will never change
    /// outcome (the same reasoning `MaxWebhookEndpoints`' own remarks give).</para>
    /// </summary>
    private static async Task<IResult> HandleYooKassaWebhookAsync(
        HttpContext httpContext,
        ProcessYooKassaWebhookHandler handler,
        CancellationToken cancellationToken)
    {
        if (!YooKassaWebhookSourceGuard.IsAllowed(httpContext.Connection.RemoteIpAddress))
        {
            // Not from one of ЮKassa's published notification networks - rejected before the body is
            // even read. `Program.cs`'s UseForwardedHeaders has already resolved RemoteIpAddress from the
            // gateway's X-Forwarded-For, so this is the real client IP, not the gateway's.
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        using var reader = new StreamReader(httpContext.Request.Body);
        var rawBody = await reader.ReadToEndAsync(cancellationToken);

        var parsed = YooKassaWebhookParser.TryParse(rawBody);
        if (parsed is null)
        {
            // From an allowlisted source but not a shape this endpoint understands - acked 200 rather
            // than rejected, since retrying will never make it parse differently.
            return Results.Ok();
        }

        await handler.HandleAsync(new ProcessYooKassaWebhook(parsed.YooKassaPaymentId), cancellationToken);

        return Results.Ok();
    }

    private static async Task<IResult> HandleCancelSubscriptionAsync(
        Guid siteId,
        Guid subscriptionId,
        CancelSubscriptionHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new CancelSubscription(user.GetOperatorId(), new SiteId(siteId), new BillingSubscriptionId(subscriptionId)),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(result.Value);
    }

    private static async Task<IResult> HandleChangeSubscriptionSeatsAsync(
        Guid siteId,
        Guid subscriptionId,
        ChangeSubscriptionSeatsRequest request,
        ChangeSubscriptionSeatsHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new ChangeSubscriptionSeats(
                user.GetOperatorId(), new SiteId(siteId), new BillingSubscriptionId(subscriptionId), request.RequestedSeats),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(result.Value);
    }

    private static async Task<IResult> HandlePurchaseAdministratorSlotAsync(
        Guid siteId,
        Guid subscriptionId,
        PurchaseAdministratorSlotRequest request,
        PurchaseAdministratorSlotHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new PurchaseAdministratorSlot(
                user.GetOperatorId(), new SiteId(siteId), new BillingSubscriptionId(subscriptionId), request.RequestedExtraAdministrators),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(result.Value);
    }

    private static async Task<IResult> HandlePurchaseChannelAddOnAsync(
        Guid siteId,
        Guid baseSubscriptionId,
        PurchaseChannelAddOnRequest request,
        PurchaseChannelAddOnHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new PurchaseChannelAddOn(
                user.GetOperatorId(), new SiteId(siteId), new BillingSubscriptionId(baseSubscriptionId), request.ChannelKind),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(result.Value);
    }

    private static async Task<IResult> HandleSetNextPeriodCompositionAsync(
        Guid siteId,
        Guid subscriptionId,
        SetNextPeriodCompositionRequest request,
        SetNextPeriodCompositionHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new SetNextPeriodComposition(
                user.GetOperatorId(), new SiteId(siteId), new BillingSubscriptionId(subscriptionId),
                request.RequestedSeats, request.RequestedExtraAdministrators),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(result.Value);
    }

    private static async Task<IResult> HandlePreviewBillingPurchaseAsync(
        Guid siteId,
        Guid subscriptionId,
        PreviewBillingPurchaseRequest request,
        PreviewBillingPurchaseHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new PreviewBillingPurchase(
                user.GetOperatorId(), new SiteId(siteId), new BillingSubscriptionId(subscriptionId), request.Kind,
                request.RequestedSeats, request.RequestedExtraAdministrators, request.ChannelKind),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(result.Value);
    }

    private static async Task<IResult> HandleGetBillingStatusAsync(
        Guid siteId, GetBillingStatusHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(new GetBillingStatus(user.GetOperatorId(), new SiteId(siteId)), cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(result.Value);
    }

    public sealed record CreateCheckoutSessionRequest(int RequestedSeats, bool SavePaymentMethod);

    public sealed record CreateTokenPaymentRequest(int RequestedSeats, string PaymentToken, bool SavePaymentMethod);

    public sealed record ChangeSubscriptionSeatsRequest(int RequestedSeats);

    public sealed record PurchaseAdministratorSlotRequest(int RequestedExtraAdministrators);

    public sealed record PurchaseChannelAddOnRequest(ChannelKind ChannelKind);

    public sealed record SetNextPeriodCompositionRequest(int RequestedSeats, int RequestedExtraAdministrators);

    /// <summary>`26-299`: the flattened, discriminated preview request - <see cref="Kind"/> decides which
    /// of <see cref="RequestedSeats"/>/<see cref="RequestedExtraAdministrators"/>/<see cref="ChannelKind"/>
    /// the handler reads (`PreviewBillingPurchase`'s own remarks).</summary>
    public sealed record PreviewBillingPurchaseRequest(
        BillingPurchaseKind Kind, int? RequestedSeats, int? RequestedExtraAdministrators, ChannelKind? ChannelKind);
}
