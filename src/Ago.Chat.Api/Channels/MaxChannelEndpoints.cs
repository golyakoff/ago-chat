using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases;
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
/// </summary>
public static class MaxChannelEndpoints
{
    public static void MapMaxChannelEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/sites/{siteId:guid}/channels/max")
            .RequireAuthorization("RequireOperatorIdentity");

        group.MapPost("", HandleConnectAsync);
        group.MapDelete("/{channelCredentialId:guid}", HandleDisconnectAsync);
    }

    private static async Task<IResult> HandleConnectAsync(
        Guid siteId,
        ConnectMaxChannelRequest request,
        RegisterChannelCredentialHandler registerHandler,
        RevokeChannelCredentialHandler revokeHandler,
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
}
