using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases;
using Ago.Chat.Application.UseCases.RegisterChannelCredential;
using Ago.Chat.Application.UseCases.RevokeChannelCredential;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Telegram;

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
/// propagates as an unhandled exception rather than a clean revoke.</para>
/// </summary>
public static class TelegramChannelEndpoints
{
    public static void MapTelegramChannelEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/sites/{siteId:guid}/channels/telegram")
            .RequireAuthorization("RequireOperatorIdentity");

        group.MapPost("", HandleConnectAsync);
        group.MapDelete("/{channelCredentialId:guid}", HandleDisconnectAsync);
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
}
