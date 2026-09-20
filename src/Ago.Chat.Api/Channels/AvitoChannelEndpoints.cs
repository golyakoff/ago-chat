using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases;
using Ago.Chat.Application.UseCases.GetChannelCredentialStatus;
using Ago.Chat.Application.UseCases.RegisterChannelCredential;
using Ago.Chat.Application.UseCases.RevokeChannelCredential;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Avito;
using Ago.Platform.Kernel;
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
///
/// <para><b>`25-177`: <see cref="HandleStatusAsync"/> now re-verifies live on every read, correcting
/// `25-65`'s own note above.</b> That note read re-asking Avito on every status read as pure cost for no
/// benefit - re-reading it while Telegram/MAX/VK/WhatsApp each gained the identical live-check richness
/// (`25-174`/`25-175`/`25-176`) found the same argument does not hold up for the one channel whose token
/// actually *expires*: a stored `Active = true` here proves nothing about whether Avito's own access token
/// still works, and unlike the other three, Avito's own access token is *expected* to expire routinely
/// (every 24 hours) rather than only on an unusual revocation. See <see cref="AvitoLiveTokenCheck"/>'s own
/// remarks for the full shape - the bounded, three-outcome check every sibling channel's own live-check
/// uses, plus the eager-refresh-on-expiry behaviour `docs/backlog/25-177-*.md`'s own Decision section
/// requires only Avito's OAuth-shaped credential to need at all.</para>
///
/// <para><b>No <see cref="Domain.ChannelCredential.PublicHandle"/> write anywhere in this endpoint, still -
/// unlike <see cref="MaxChannelEndpoints.HandleStatusAsync"/>/<see cref="WhatsAppChannelEndpoints.HandleStatusAsync"/>'s
/// own live-check backfill.</b> <see cref="AvitoLiveTokenCheck"/>'s own outcome type carries no handle
/// field at all - `25-147`'s decision that Avito has no provider-documented public deep-link to store is
/// unaffected by this item; this endpoint's live check adds only richness about whether the token works,
/// never a fact this credential never had anywhere to keep.</para>
/// </summary>
public static class AvitoChannelEndpoints
{
    public static void MapAvitoChannelEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/sites/{siteId:guid}/channels/avito")
            .RequireAuthorization("RequireOperatorIdentity");

        group.MapGet("", HandleStatusAsync);
        group.MapPost("", HandleConnectAsync);
        group.MapDelete("/{channelCredentialId:guid}", HandleDisconnectAsync);
    }

    private static async Task<IResult> HandleStatusAsync(
        Guid siteId,
        GetChannelCredentialStatusHandler statusHandler,
        IChannelCredentialRepository credentials,
        IChannelCredentialCipher cipher,
        AvitoApiClient avitoApiClient,
        IOptions<AvitoApiOptions> avitoOptions,
        IClock clock,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var site = new SiteId(siteId);

        var status = await statusHandler.HandleAsync(
            new GetChannelCredentialStatus(user.GetOperatorId(), site, ChannelKind.Avito), cancellationToken);
        if (status.IsFailure)
        {
            return status.Error!.Value.ToProblem(httpContext);
        }

        if (status.Value.ChannelCredentialId is not { } credentialId)
        {
            return Results.Ok(AvitoChannelStatusResponse.NotConnected(clock.UtcNow));
        }

        // `GetChannelCredentialStatusHandler` already confirmed this operator may manage this site's
        // channels and that this id belongs to it - this second repository call exists only to reach
        // TokenCiphertext/RefreshTokenCiphertext, which that channel-neutral handler's own result
        // deliberately never carries (the identical reason every sibling live-check endpoint makes this
        // same second call). A credential revoked between the two calls (an operator double-clicking
        // Disconnect in another tab) is not an error - it means "no longer connected", answered the same
        // way as if it had never existed.
        var credential = await credentials.GetByIdAsync(credentialId, cancellationToken);
        if (credential is null || !credential.Active)
        {
            return Results.Ok(AvitoChannelStatusResponse.NotConnected(clock.UtcNow));
        }

        var outcome = await AvitoLiveTokenCheck.RunAsync(
            avitoApiClient, credentials, cipher, avitoOptions.Value, credential, AvitoLiveTokenCheck.Timeout, cancellationToken);

        // `25-177`/`25-147`: deliberately no PublicHandle write here - see this class's own remarks above.

        return Results.Ok(new AvitoChannelStatusResponse(
            Connected: true,
            ChannelCredentialId: credentialId.Value,
            CreatedAt: status.Value.CreatedAt,
            Verified: outcome.Unreachable ? null : outcome.Ok,
            Unreachable: outcome.Unreachable,
            RefusalReason: outcome.RefusalReason,
            CheckedAt: clock.UtcNow));
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

        // `25-147`: Avito gets no PublicHandle, deliberately, and never will through this endpoint.
        // AvitoUserInfoSelf.Id is a numeric seller account id - Avito documents no public deep-link URL
        // built from it (or from anything else this credential holds), so storing it here would only
        // invite a future reader to assume a link exists that Avito itself never promised. Left null
        // rather than reusing ProviderAccountId's own value the way VK's read-time derivation does.
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

    /// <summary>`25-177`: the same seven fields <see cref="WhatsAppChannelEndpoints.WhatsAppChannelStatusResponse"/>/
    /// <see cref="VkChannelEndpoints.VkChannelStatusResponse"/> already carry, repeated here rather than
    /// extracted into a shared shape - this codebase's own established convention of one independent
    /// response record per channel (`WhatsAppChannelEndpoints.WhatsAppChannelStatusResponse`'s own remarks
    /// give the fuller reasoning). No field is shaped like a secret, the same guarantee
    /// <see cref="ConnectAvitoChannelResponse"/> already makes.</summary>
    public sealed record AvitoChannelStatusResponse(
        bool Connected,
        Guid? ChannelCredentialId,
        DateTimeOffset? CreatedAt,
        bool? Verified,
        bool Unreachable,
        string? RefusalReason,
        DateTimeOffset CheckedAt)
    {
        public static AvitoChannelStatusResponse NotConnected(DateTimeOffset checkedAt) =>
            new(Connected: false, ChannelCredentialId: null, CreatedAt: null, Verified: null, Unreachable: false,
                RefusalReason: null, CheckedAt: checkedAt);
    }
}
