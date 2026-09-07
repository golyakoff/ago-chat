using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases;
using Ago.Chat.Application.UseCases.GetChannelCredentialStatus;
using Ago.Chat.Application.UseCases.RegisterChannelCredential;
using Ago.Chat.Application.UseCases.RevokeChannelCredential;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Telegram;
using Ago.Platform.Kernel;

namespace Ago.Chat.Api.Channels;

/// <summary>
/// `14-07`/`adr/0069`: the console's own Telegram connection flow - the same
/// <c>"RequireOperatorIdentity"</c> policy <see cref="MaxChannelEndpoints"/> already uses, and
/// considerably simpler than that one: Telegram has no subscribe-a-webhook step
/// (<see cref="TelegramBotApiOptions"/>'s own remarks on why this channel has no webhook path at all),
/// so this endpoint never needs to sequence "persist the credential, then tell the provider where to
/// deliver" the way <see cref="MaxChannelEndpoints"/> must.
///
/// <para><b>Why this endpoint still calls Telegram once, via <c>getMe</c>, even with no required side
/// effect.</b> <c>RegisterChannelCredentialHandler</c> validates only the token's <em>shape</em>
/// (length, non-empty) - it cannot know whether the token is one Telegram will actually accept, because
/// calling Telegram is provider-shaped work `adr/0006`'s "largest common denominator" keeps out of that
/// channel-neutral handler. Skipping the <c>getMe</c> check would mean a shop only discovers a typo'd or
/// already-revoked token the first time an operator tries to reply and the send silently comes back
/// <c>Refused</c> - a materially worse UX than <see cref="MaxChannelEndpoints"/>'s own "reject a bad
/// token immediately" flow, for the cost of one extra GET request made exactly once, at registration.
/// This item's own judgement is that the extra round trip is worth it for the identical reason MAX's own
/// endpoint decided it was.</para>
///
/// <para><b>What happens when Telegram rejects the token.</b> A bad or already-revoked token surfaces
/// the moment this endpoint calls <c>getMe</c> - <see cref="TelegramGetMeResult.Ok"/> is
/// <see langword="false"/> - and this endpoint reacts by revoking the credential it just created, the
/// same rollback <see cref="MaxChannelEndpoints"/> performs on a
/// <see cref="MaxSubscriptionRejectedException"/>. A <em>transient</em> fault (Telegram, or this
/// deployment's own outbound relay, unreachable) is deliberately not rolled back the same way - see
/// <see cref="TelegramApiClient.GetMeAsync"/>'s own remarks for why that distinction matters here and
/// propagates as an unhandled exception rather than a clean revoke. This connect-time call has no bound
/// beyond <c>HttpClient</c>'s own 100-second default - acceptable here because it is a deliberate action
/// an operator just took once, unlike the status read below.</para>
///
/// <para><b>`23-36`: <c>GET</c> asks Telegram again, every time, rather than trusting
/// <see cref="Domain.ChannelCredential.Active"/> alone.</b> A stored `true` only ever proves the token
/// was accepted once, at registration - it says nothing about a bot deleted or a token reset at
/// Telegram's own side since, and nothing on this codebase's outbound path notices that until an
/// operator's next reply comes back <c>Refused</c> (`adr/0069`'s own recorded gap: "no tenant-facing
/// notification mechanism yet"). `23-36`'s own brief is explicit that this matters: "a tenant who
/// pastes a token and sees a green tick, while the bot is in fact unreachable, is worse off than one
/// told nothing." So this handler re-asks Telegram's own <c>getMe</c> on every status read, live,
/// through the same <see cref="TelegramApiClient"/> the connect flow above already uses - never
/// revoking on a bad answer by itself (only an explicit disconnect does that; see
/// <see cref="HandleDisconnectAsync"/>), only reporting honestly what the provider just said.</para>
///
/// <para><b>Unlike the connect-time call above, this one is bounded.</b> <see cref="TelegramLiveTokenCheck"/>
/// (its own remarks have the full reasoning) wraps the call with a 5-second timeout, matched to
/// <c>ChatModule.ConfigureChannelResilienceDefaults</c>'s own value rather than invented - a status read
/// runs on every load of this screen, unlike the connect-time call, so it cannot inherit
/// <c>HttpClient</c>'s 100-second default the way that one-off call safely can. A timeout, or any other
/// transient failure to reach Telegram (this deployment's own SOCKS5 relay included), reports as
/// <see cref="TelegramChannelStatusResponse.Unreachable"/> - "could not reach Telegram just now" - kept
/// structurally distinct from a terminal refusal ("Telegram says this token is not valid"), because a
/// tenant acts on the two differently: wait and retry versus get a new token.</para>
///
/// <para><b>This is a second caller of <see cref="IChannelCredentialCipher.Decrypt"/> outside an
/// outbound send</b> - `adr/0069`'s own text says "no use case this item ships calls Decrypt from a
/// read path; the only caller is the outbound send", a claim already narrower than reality even before
/// this item (`TelegramLongPollingService`/`MaxLongPollingService` both decrypt to poll, which is
/// inbound, not outbound send). `adr/0143` records why a live status check is a third, deliberate
/// caller: showing evidence rather than a stored flag is this item's own point, and the token
/// never leaves this method - only two booleans and, on failure, the provider's own refusal text reach
/// the response.</para>
/// </summary>
public static class TelegramChannelEndpoints
{
    public static void MapTelegramChannelEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/sites/{siteId:guid}/channels/telegram")
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
        TelegramApiClient telegramApiClient,
        IClock clock,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var site = new SiteId(siteId);

        var status = await statusHandler.HandleAsync(
            new GetChannelCredentialStatus(user.GetOperatorId(), site, ChannelKind.Telegram), cancellationToken);
        if (status.IsFailure)
        {
            return status.Error!.Value.ToProblem(httpContext);
        }

        if (status.Value.ChannelCredentialId is not { } credentialId)
        {
            return Results.Ok(TelegramChannelStatusResponse.NotConnected(clock.UtcNow));
        }

        // `GetChannelCredentialStatusHandler` already confirmed this operator may manage this site's
        // channels and that this id belongs to it - this second repository call exists only to reach
        // TokenCiphertext, which that channel-neutral handler's own result deliberately never carries
        // (this file's own remarks above on why a live check needs it regardless). A credential
        // revoked between the two calls (an operator double-clicking Disconnect in another tab) is not
        // an error - it means "no longer connected", answered the same way as if it had never existed.
        var credential = await credentials.GetByIdAsync(credentialId, cancellationToken);
        if (credential is null || !credential.Active)
        {
            return Results.Ok(TelegramChannelStatusResponse.NotConnected(clock.UtcNow));
        }

        var token = cipher.Decrypt(credential.TokenCiphertext);
        var outcome = await TelegramLiveTokenCheck.RunAsync(
            telegramApiClient, token, TelegramLiveTokenCheck.Timeout, cancellationToken);

        return Results.Ok(new TelegramChannelStatusResponse(
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
        ConnectTelegramChannelRequest request,
        RegisterChannelCredentialHandler registerHandler,
        RevokeChannelCredentialHandler revokeHandler,
        TelegramApiClient telegramApiClient,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var site = new SiteId(siteId);

        var registered = await registerHandler.HandleAsync(
            new RegisterChannelCredential(user.GetOperatorId(), site, ChannelKind.Telegram, request.Token),
            cancellationToken);
        if (registered.IsFailure)
        {
            return registered.Error!.Value.ToProblem(httpContext);
        }

        var credentialId = registered.Value.ChannelCredentialId;

        var verified = await telegramApiClient.GetMeAsync(request.Token, cancellationToken);
        if (!verified.Ok)
        {
            // Roll back rather than leave a credential Telegram itself just told us is unusable - the
            // same discipline MaxChannelEndpoints applies on a MaxSubscriptionRejectedException.
            await revokeHandler.HandleAsync(
                new RevokeChannelCredential(credentialId, user.GetOperatorId(), site), cancellationToken);
            return ConversationErrors.ChannelInvalidToken(verified.RefusalReason!).ToProblem(httpContext);
        }

        return Results.Created(
            $"/api/v1/sites/{siteId}/channels/telegram/{credentialId.Value}",
            new ConnectTelegramChannelResponse(credentialId.Value, registered.Value.CreatedAt));
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

    public sealed record ConnectTelegramChannelRequest(string Token);

    /// <summary>Deliberately carries nothing the shop entered back - no token (`adr/0069`'s "the
    /// console never shows it back"). Only what the console needs to render a connected state and offer
    /// a disconnect action - the same shape as <c>MaxChannelEndpoints.ConnectMaxChannelResponse</c>.</summary>
    public sealed record ConnectTelegramChannelResponse(Guid ChannelCredentialId, DateTimeOffset CreatedAt);

    /// <summary>
    /// `23-36`: no field this type could carry is shaped like the shop's own token - `Connected`,
    /// `ChannelCredentialId` and `CreatedAt` are the same three facts <see cref="ConnectTelegramChannelResponse"/>
    /// already exposes; `Verified`/`Unreachable`/`RefusalReason`/`CheckedAt` are this endpoint's own
    /// addition, and none of the four can hold a secret - `Verified`/`Unreachable` are booleans,
    /// `RefusalReason` is Telegram's own refusal text (never anything this codebase generated from the
    /// token), `CheckedAt` is a timestamp.
    ///
    /// <para><b><c>Verified</c>/<c>Unreachable</c>/<c>RefusalReason</c> together carry exactly three
    /// live-check states, mirroring <see cref="TelegramLiveCheckOutcome"/> one for one</b> - see that
    /// type's own remarks. <c>Verified: true</c> (the token works); <c>Verified: false, RefusalReason</c>
    /// set (Telegram looked at the token and refused it - a tenant needs a new token);
    /// <c>Unreachable: true, Verified: null, RefusalReason: null</c> (the check could not complete at
    /// all, in time or otherwise - a tenant should wait and retry, not assume the token is bad).
    /// <c>Connected: false</c> is a fourth, unrelated state (nothing to check yet) and also carries
    /// <c>Verified: null</c>, distinguished from "unreachable" only by <c>Connected</c> itself - the
    /// live check is never attempted when there is no credential to check.</para>
    /// </summary>
    public sealed record TelegramChannelStatusResponse(
        bool Connected,
        Guid? ChannelCredentialId,
        DateTimeOffset? CreatedAt,
        bool? Verified,
        bool Unreachable,
        string? RefusalReason,
        DateTimeOffset CheckedAt)
    {
        public static TelegramChannelStatusResponse NotConnected(DateTimeOffset checkedAt) =>
            new(Connected: false, ChannelCredentialId: null, CreatedAt: null, Verified: null, Unreachable: false,
                RefusalReason: null, CheckedAt: checkedAt);
    }
}
