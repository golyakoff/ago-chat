using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases;
using Ago.Chat.Application.UseCases.RegisterChannelCredential;
using Ago.Chat.Application.UseCases.RevokeChannelCredential;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Avito;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Api.Channels;

/// <summary>
/// `14-11`/`adr/0069`: the console's own Avito connection flow - the same
/// <c>"RequireOperatorIdentity"</c> policy every other channel's own connect endpoint already uses.
///
/// <para><b>A hybrid of VK's and MAX's own sequencing, not a repeat of either.</b> Like VK's own
/// <c>groups.getById</c> validation, this endpoint calls <see cref="AvitoApiClient.GetSelfAsync"/>
/// <em>before</em> ever writing a <see cref="ChannelCredential"/> row - a bad or expired token is
/// refused with nothing written to storage. Unlike VK, though, Avito's own webhook subscription needs
/// this credential's own generated id (to build a per-credential callback URL -
/// <c>AvitoWebhookEndpoints</c>' own remarks), which does not exist before
/// <see cref="RegisterChannelCredentialHandler"/> creates the row - so, like MAX's own
/// <c>POST /subscriptions</c> step, the subscribe call happens <em>after</em> the row exists, and a
/// rejection there rolls the credential back rather than leaving a known-broken one active
/// (<c>MaxChannelEndpoints</c>' own precedent for the identical roll-back reasoning).</para>
///
/// <para><b>The connect request carries two tokens, not one - the one shape difference from every other
/// channel's own connect request.</b> Avito's own credential is a real OAuth 2 authorization-code
/// access/refresh pair (<see cref="Domain.ChannelCredential.RefreshTokenCiphertext"/>'s own remarks),
/// not a single durable bot/community token. Building the full OAuth redirect-and-consent dance (a
/// redirect to Avito's own consent screen, a callback exchanging a <c>code</c> for tokens) is out of this
/// item's own scope - a genuinely new mechanism this codebase has never needed for any other channel, and
/// exactly the kind of premature generalization CLAUDE.md warns against building for a single provider.
/// What this endpoint accepts instead matches every other channel's own "paste a credential in" shape:
/// the shop completes Avito's own OAuth consent flow once, outside AGO (the same way a MAX bot token or a
/// VK community token is obtained from each provider's own console today), and an operator pastes both
/// resulting values in.</para>
/// </summary>
public static class AvitoChannelEndpoints
{
    public static void MapAvitoChannelEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/sites/{siteId:guid}/channels/avito")
            .RequireAuthorization("RequireOperatorIdentity");

        group.MapPost("", HandleConnectAsync);
        group.MapDelete("/{channelCredentialId:guid}", HandleDisconnectAsync);
    }

    private static async Task<IResult> HandleConnectAsync(
        Guid siteId,
        ConnectAvitoChannelRequest request,
        RegisterChannelCredentialHandler registerHandler,
        RevokeChannelCredentialHandler revokeHandler,
        AvitoApiClient avitoApiClient,
        IOptions<AvitoApiOptions> avitoOptions,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (avitoOptions.Value.PublicWebhookBaseUrl is not { } publicBase)
        {
            // VkChannelEndpoints' own precedent: unlike MAX, there is no polling fallback to skip to -
            // without a public URL there is nothing this system could ever hand Avito to call back to.
            return ConversationErrors.ChannelNotAvailable(
                "Avito is not available on this deployment - no public webhook base URL is configured.").ToProblem(httpContext);
        }

        var user = httpContext.User;
        var site = new SiteId(siteId);

        AvitoUserInfoSelf self;
        try
        {
            self = await avitoApiClient.GetSelfAsync(request.AccessToken, cancellationToken);
        }
        catch (AvitoApiCallException ex)
        {
            return ConversationErrors.ChannelInvalidToken(ex.Message).ToProblem(httpContext);
        }

        var registered = await registerHandler.HandleAsync(
            new RegisterChannelCredential(
                user.GetOperatorId(), site, ChannelKind.Avito, request.AccessToken,
                ProviderAccountId: self.Id.ToString(), RefreshToken: request.RefreshToken),
            cancellationToken);
        if (registered.IsFailure)
        {
            return registered.Error!.Value.ToProblem(httpContext);
        }

        var credentialId = registered.Value.ChannelCredentialId;
        var callbackUrl = new Uri(publicBase, $"webhooks/avito/{credentialId.Value}?{AvitoWebhookEndpoints.SecretQueryParamName}={Uri.EscapeDataString(registered.Value.WebhookSecret)}");

        try
        {
            await avitoApiClient.SubscribeWebhookAsync(request.AccessToken, callbackUrl, cancellationToken);
        }
        catch (AvitoApiCallException ex)
        {
            // Roll back rather than leave a credential Avito itself just told us is unusable -
            // MaxChannelEndpoints' own precedent for the identical reasoning.
            await revokeHandler.HandleAsync(
                new RevokeChannelCredential(credentialId, user.GetOperatorId(), site), cancellationToken);
            return ConversationErrors.ChannelInvalidToken(ex.Message).ToProblem(httpContext);
        }

        return Results.Created(
            $"/api/v1/sites/{siteId}/channels/avito/{credentialId.Value}",
            new ConnectAvitoChannelResponse(credentialId.Value, registered.Value.CreatedAt));
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

    /// <summary><see cref="AccessToken"/>/<see cref="RefreshToken"/> are the pair a shop obtains by
    /// completing Avito's own OAuth consent flow once, outside AGO - this class's own remarks explain why
    /// AGO does not build the redirect/consent dance itself.</summary>
    public sealed record ConnectAvitoChannelRequest(string AccessToken, string RefreshToken);

    /// <summary>Deliberately carries nothing the shop entered back, and no webhook secret or callback URL
    /// - <c>ConnectMaxChannelResponse</c>'s own shape, not <c>ConnectVkChannelResponse</c>'s: Avito's own
    /// webhook, like MAX's, is registered programmatically by this endpoint, so there is nothing a human
    /// needs to paste anywhere.</summary>
    public sealed record ConnectAvitoChannelResponse(Guid ChannelCredentialId, DateTimeOffset CreatedAt);
}
