using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases;
using Ago.Chat.Application.UseCases.RegisterChannelCredential;
using Ago.Chat.Application.UseCases.RevokeChannelCredential;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Vk;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Api.Channels;

/// <summary>
/// `14-08`/`adr/0069`: the console's own VK connection flow - the same
/// <c>"RequireOperatorIdentity"</c> policy <see cref="MaxChannelEndpoints"/>/<see cref="TelegramChannelEndpoints"/>
/// already use.
///
/// <para><b>Why this endpoint validates the token, and discovers VK's own community id, <em>before</em>
/// ever writing a <see cref="ChannelCredential"/> row - unlike both precedents, which must create the
/// row first and roll it back on a rejection.</b> MAX's own subscribe call needs the webhook secret this
/// system generates, which does not exist before <see cref="RegisterChannelCredentialHandler"/> creates
/// the row; Telegram's <c>getMe</c> check does not strictly need that ordering but follows it anyway for
/// consistency. VK's <c>groups.getById</c> needs nothing but the raw token the operator just typed in -
/// no dependency on anything this system generates - so this endpoint calls it first. A bad or
/// already-revoked token is refused with nothing ever written to storage, which is simpler than either
/// precedent's create-then-roll-back dance, not a shortcut: it is only possible because of what VK's own
/// API happens to need, not a pattern this item is proposing MAX or Telegram should have used
/// instead.</para>
///
/// <para><b>Why the response carries the webhook secret and the callback URL in plaintext, unlike
/// <see cref="MaxChannelEndpoints.ConnectMaxChannelResponse"/>/<see cref="TelegramChannelEndpoints.ConnectTelegramChannelResponse"/>,
/// which carry neither.</b> MAX registers its own webhook programmatically
/// (<c>POST /subscriptions</c>); Telegram has no webhook at all. VK's Callback API is configured by a
/// human, in VK's own community settings UI, pasting in a URL and a secret key - so the human has to be
/// given both. <c>RegisteredChannelCredential.WebhookSecret</c>'s own remarks record why this is not a
/// new exception to `adr/0069`'s "console never shows it back": that rule is about the shop's own token,
/// never about a secret AGO generated for the shop's benefit, and this is the first channel where a
/// human - not an API call - is the one who needs it.</para>
/// </summary>
public static class VkChannelEndpoints
{
    public static void MapVkChannelEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/sites/{siteId:guid}/channels/vk")
            .RequireAuthorization("RequireOperatorIdentity");

        group.MapPost("", HandleConnectAsync);
        group.MapDelete("/{channelCredentialId:guid}", HandleDisconnectAsync);
    }

    private static async Task<IResult> HandleConnectAsync(
        Guid siteId,
        ConnectVkChannelRequest request,
        RegisterChannelCredentialHandler registerHandler,
        VkApiClient vkApiClient,
        IOptions<VkBotApiOptions> vkOptions,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (vkOptions.Value.PublicWebhookBaseUrl is not { } publicBase)
        {
            // VkBotApiOptions' own remarks: unlike MAX, there is no fallback inbound mechanism to skip
            // to - without a public URL there is nothing this system could ever hand VK to call back to,
            // so connecting is refused outright rather than silently accepting a token nothing will ever
            // deliver to.
            return ConversationErrors.ChannelNotAvailable(
                "VK is not available on this deployment - no public webhook base URL is configured.").ToProblem(httpContext);
        }

        var user = httpContext.User;
        var site = new SiteId(siteId);

        VkGroupInfo groupInfo;
        try
        {
            groupInfo = await vkApiClient.GetGroupInfoAsync(request.Token, cancellationToken);
        }
        catch (VkApiCallException ex)
        {
            return ConversationErrors.ChannelInvalidToken(ex.Message).ToProblem(httpContext);
        }

        var registered = await registerHandler.HandleAsync(
            new RegisterChannelCredential(
                user.GetOperatorId(), site, ChannelKind.Vk, request.Token, groupInfo.GroupId.ToString()),
            cancellationToken);
        if (registered.IsFailure)
        {
            return registered.Error!.Value.ToProblem(httpContext);
        }

        var credentialId = registered.Value.ChannelCredentialId;
        var callbackUrl = new Uri(publicBase, $"webhooks/vk/{credentialId.Value}");

        return Results.Created(
            $"/api/v1/sites/{siteId}/channels/vk/{credentialId.Value}",
            new ConnectVkChannelResponse(credentialId.Value, registered.Value.CreatedAt, callbackUrl.ToString(), registered.Value.WebhookSecret));
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

    public sealed record ConnectVkChannelRequest(string Token);

    /// <summary><see cref="CallbackUrl"/>/<see cref="WebhookSecret"/> are what an operator pastes into
    /// VK's own community Callback API settings page - see this class's own remarks for why this
    /// response, unlike MAX's/Telegram's, carries them at all.</summary>
    public sealed record ConnectVkChannelResponse(
        Guid ChannelCredentialId, DateTimeOffset CreatedAt, string CallbackUrl, string WebhookSecret);
}
