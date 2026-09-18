using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases;
using Ago.Chat.Application.UseCases.GetChannelCredentialStatus;
using Ago.Chat.Application.UseCases.RegisterChannelCredential;
using Ago.Chat.Application.UseCases.RevokeChannelCredential;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.MaxBot;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Api.Channels;

/// <summary>
/// `14-02`/`adr/0069`: the console's own MAX connection flow - operator-only, the same
/// <c>"RequireOperatorIdentity"</c> policy <see cref="Webhooks.WebhookEndpoints"/> already uses.
///
/// <para><b>Why the MAX-specific <c>POST /subscriptions</c> call happens here, in a host, rather than
/// inside <see cref="RegisterChannelCredentialHandler"/>.</b> That handler is deliberately
/// channel-neutral (its own remarks) - `adr/0006`'s "largest common denominator" keeps a provider's own
/// admin API below Infrastructure, the identical reasoning that keeps a provider's own message shape out
/// of <see cref="Application.Abstractions.IInboundChannelAdapter"/>. Only a host may reference
/// <c>Ago.Chat.Infrastructure.MaxBot</c> directly, so only a host can sequence "persist the credential,
/// then tell MAX where to deliver" - and the sequencing has to run in that order, because the webhook
/// secret MAX needs to be told does not exist until the handler generates it.</para>
///
/// <para><b>What happens when MAX rejects the token.</b> A bad or already-revoked token surfaces the
/// moment this endpoint tries to subscribe - <see cref="MaxSubscriptionRejectedException"/> - and this
/// endpoint reacts by revoking the credential it just created, so a known-bad token is never left active
/// in storage waiting to fail again the first time an operator sends a reply. When no
/// <see cref="MaxBotApiOptions.PublicWebhookBaseUrl"/> is configured (the local compose loop, which has
/// no public HTTPS endpoint for MAX to call), this step is skipped entirely and the credential is trusted
/// on the strength of nothing yet - <c>MaxLongPollingService</c> is what will discover, on its own next
/// poll, whether the token actually works.</para>
///
/// <para><b>`25-09`: <see cref="HandleStatusAsync"/> exists, but it does not do what
/// <see cref="Api.Channels.TelegramChannelEndpoints.HandleStatusAsync"/> does.</b> That endpoint
/// re-verifies the token live on every read because Telegram exposes a cheap, side-effect-free
/// <c>getMe</c> call. <b>`25-147`'s own correction: this comment used to claim MAX's public API "has no
/// equivalent" - that was stale and wrong even before this item.</b> MAX's Bot API has always had a
/// <c>GET /me</c> (dev.max.ru/docs-api), needing only the bot token every other call here already
/// attaches - <see cref="MaxApiClient.GetMeAsync"/>'s own remarks have the full account, and
/// <see cref="HandleConnectAsync"/> now calls it, best-effort, to capture the bot's own
/// <see cref="Domain.ChannelCredential.PublicHandle"/>. What remains genuinely true, and is the actual
/// reason <see cref="HandleStatusAsync"/> stays the narrow shape below: MAX's only other credential-shaped
/// calls are <c>POST /subscriptions</c> (a write, changing the live webhook registration - not safe to
/// repeat on every page load of this screen) and <c>GET /updates</c> (the long-polling read
/// <c>MaxLongPollingService</c> already owns exclusively - a second, unsynchronized caller would race it
/// for the same marker). <c>GET /me</c> itself has no such hazard - it is exactly as safe to repeat as
/// Telegram's own <c>getMe</c> - but this item deliberately does not use it to give
/// <see cref="HandleStatusAsync"/> Telegram-style live-verification parity (`23-36`/`adr/0143`): that is a
/// real, separate, valuable item of its own, named here so it is not silently rediscovered, not built as
/// a side effect of this one. So this endpoint still reports only what
/// <see cref="GetChannelCredentialStatusHandler"/> already answers for every channel: whether an active
/// credential row exists, and since when.</para>
/// </summary>
public static class MaxChannelEndpoints
{
    public static void MapMaxChannelEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/sites/{siteId:guid}/channels/max")
            .RequireAuthorization("RequireOperatorIdentity");

        group.MapGet("", HandleStatusAsync);
        group.MapPost("", HandleConnectAsync);
        group.MapDelete("/{channelCredentialId:guid}", HandleDisconnectAsync);
    }

    private static async Task<IResult> HandleStatusAsync(
        Guid siteId,
        GetChannelCredentialStatusHandler statusHandler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var site = new SiteId(siteId);

        var status = await statusHandler.HandleAsync(
            new GetChannelCredentialStatus(user.GetOperatorId(), site, ChannelKind.Max), cancellationToken);
        if (status.IsFailure)
        {
            return status.Error!.Value.ToProblem(httpContext);
        }

        return Results.Ok(status.Value.ChannelCredentialId is { } credentialId
            ? new MaxChannelStatusResponse(Connected: true, ChannelCredentialId: credentialId.Value, CreatedAt: status.Value.CreatedAt)
            : MaxChannelStatusResponse.NotConnected);
    }

    private static async Task<IResult> HandleConnectAsync(
        Guid siteId,
        ConnectMaxChannelRequest request,
        RegisterChannelCredentialHandler registerHandler,
        RevokeChannelCredentialHandler revokeHandler,
        IChannelCredentialRepository credentials,
        MaxApiClient maxApiClient,
        IOptions<MaxBotApiOptions> maxOptions,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var site = new SiteId(siteId);

        var registered = await registerHandler.HandleAsync(
            new RegisterChannelCredential(user.GetOperatorId(), site, ChannelKind.Max, request.Token),
            cancellationToken);
        if (registered.IsFailure)
        {
            return registered.Error!.Value.ToProblem(httpContext);
        }

        var credentialId = registered.Value.ChannelCredentialId;

        if (maxOptions.Value.PublicWebhookBaseUrl is { } publicBase)
        {
            var callbackUrl = new Uri(publicBase, $"webhooks/max/{credentialId.Value}");
            try
            {
                await maxApiClient.SubscribeWebhookAsync(request.Token, callbackUrl, registered.Value.WebhookSecret, cancellationToken);
            }
            catch (MaxSubscriptionRejectedException ex)
            {
                // Roll back rather than leave a credential MAX itself just told us is unusable -
                // this endpoint's own remarks on why a bad token must not be left active.
                await revokeHandler.HandleAsync(
                    new RevokeChannelCredential(credentialId, user.GetOperatorId(), site), cancellationToken);
                return ConversationErrors.ChannelInvalidToken(ex.Message).ToProblem(httpContext);
            }
        }

        // `25-147`: best-effort - see MaxApiClient.GetMeAsync's own remarks for why this call is never a
        // connect-time gate the way Telegram's identical call already is. A refusal or a transient fault
        // here costs the tenant nothing but a missing public handle; the connect itself already
        // succeeded above.
        try
        {
            var me = await maxApiClient.GetMeAsync(request.Token, cancellationToken);
            if (me.Ok && me.Username is { Length: > 0 } username)
            {
                var credential = await credentials.GetByIdAsync(credentialId, cancellationToken);
                credential?.SetPublicHandle(username);
                if (credential is not null)
                {
                    await credentials.SaveAsync(credential, cancellationToken);
                }
            }
        }
        catch (HttpRequestException)
        {
            // Transient - MAX itself, or this deployment's own outbound path, did not answer at all.
            // Nothing to roll back: the credential this endpoint just created is fully usable without a
            // public handle, and a later reconnect or a future live-status read is this item's own
            // named, deferred way to retry (see this class's own remarks on 23-36 parity).
        }

        return Results.Created(
            $"/api/v1/sites/{siteId}/channels/max/{credentialId.Value}",
            new ConnectMaxChannelResponse(credentialId.Value, registered.Value.CreatedAt));
    }

    private static async Task<IResult> HandleDisconnectAsync(
        Guid siteId,
        Guid channelCredentialId,
        RevokeChannelCredentialHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new RevokeChannelCredential(new ChannelCredentialId(channelCredentialId), user.GetOperatorId(), new SiteId(siteId)),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.NoContent();
    }

    public sealed record ConnectMaxChannelRequest(string Token);

    /// <summary>Deliberately carries nothing the shop entered back - no token, no webhook secret
    /// (`adr/0069`'s "the console never shows it back"). Only what the console needs to render a
    /// connected state and offer a disconnect action.</summary>
    public sealed record ConnectMaxChannelResponse(Guid ChannelCredentialId, DateTimeOffset CreatedAt);

    /// <summary>
    /// `25-09`: deliberately three fields, not <see cref="Api.Channels.TelegramChannelEndpoints.TelegramChannelStatusResponse"/>'s
    /// seven - there is no <c>Verified</c>/<c>Unreachable</c>/<c>RefusalReason</c>/<c>CheckedAt</c> here
    /// because <see cref="HandleStatusAsync"/> never asks MAX anything (this type's own remarks above).
    /// No field is shaped like a secret, the same guarantee <see cref="ConnectMaxChannelResponse"/> already
    /// makes.
    /// </summary>
    public sealed record MaxChannelStatusResponse(bool Connected, Guid? ChannelCredentialId, DateTimeOffset? CreatedAt)
    {
        public static readonly MaxChannelStatusResponse NotConnected = new(Connected: false, ChannelCredentialId: null, CreatedAt: null);
    }
}
