using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases;
using Ago.Chat.Application.UseCases.RegisterChannelCredential;
using Ago.Chat.Application.UseCases.RevokeChannelCredential;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.WhatsApp;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Api.Channels;

/// <summary>
/// `14-10`/`adr/0069`: the console's own WhatsApp connection flow - the same
/// <c>"RequireOperatorIdentity"</c> policy <see cref="MaxChannelEndpoints"/>/<see cref="TelegramChannelEndpoints"/>/
/// <see cref="VkChannelEndpoints"/> already use.
///
/// <para><b>Why the connect request carries both a token and a <c>phoneNumberId</c>, unlike every
/// precedent.</b> MAX's/Telegram's bot tokens are self-addressing; VK's <c>groups.getById</c> discovers
/// the community id from the token alone, with no <c>group_id</c> parameter. WhatsApp offers neither: a
/// WhatsApp Business Account can hold more than one phone number, and Meta's own Graph API has no "which
/// number does this token mean by default" call - so the operator supplies the number's own
/// <c>phone_number_id</c> directly (read off Meta's own App Dashboard, or the Embedded Signup response),
/// and <see cref="WhatsAppApiClient.GetPhoneNumberAsync"/>'s own call validates that the token is actually
/// authorized for that specific number, rather than discovering it independently the way VK's own call
/// does. <see cref="WhatsAppApiClient.GetPhoneNumberAsync"/>'s own remarks have the full contrast.</para>
///
/// <para><b>Validated before ever writing a <see cref="ChannelCredential"/> row - the identical ordering
/// <see cref="VkChannelEndpoints"/> already established, for the identical reason.</b> WhatsApp's own
/// phone-number lookup needs nothing this system generates, so a bad token or a phone number id the
/// token cannot access is refused with nothing ever written to storage.</para>
///
/// <para><b>The response carries neither a callback URL nor a webhook secret, unlike
/// <see cref="VkChannelEndpoints.ConnectVkChannelResponse"/> and like
/// <see cref="MaxChannelEndpoints.ConnectMaxChannelResponse"/>/<see cref="TelegramChannelEndpoints.ConnectTelegramChannelResponse"/>.</b>
/// WhatsApp's inbound webhook is App-wide, configured once against AGO's own Meta App
/// (<see cref="WhatsAppBotApiOptions"/>' own remarks) - there is no per-credential URL or secret for a
/// human to paste anywhere, so this endpoint has nothing of that shape to hand back.</para>
///
/// <para><b>Connecting is refused outright while this deployment has not configured
/// <see cref="WhatsAppBotApiOptions.AppSecret"/>/<see cref="WhatsAppBotApiOptions.VerifyToken"/> -
/// the identical reasoning <see cref="VkChannelEndpoints"/>' own <c>PublicWebhookBaseUrl</c> check
/// applies.</b> Without either, no inbound WhatsApp delivery to this deployment could ever be
/// authenticated and accepted, so accepting a token now would silently promise a channel that can send
/// but never receive.</para>
/// </summary>
public static class WhatsAppChannelEndpoints
{
    public static void MapWhatsAppChannelEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/sites/{siteId:guid}/channels/whatsapp")
            .RequireAuthorization("RequireOperatorIdentity");

        group.MapPost("", HandleConnectAsync);
        group.MapDelete("/{channelCredentialId:guid}", HandleDisconnectAsync);
    }

    private static async Task<IResult> HandleConnectAsync(
        Guid siteId,
        ConnectWhatsAppChannelRequest request,
        RegisterChannelCredentialHandler registerHandler,
        WhatsAppApiClient whatsAppApiClient,
        IOptions<WhatsAppBotApiOptions> whatsAppOptions,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var options = whatsAppOptions.Value;
        if (options.AppSecret is not { Length: > 0 } || options.VerifyToken is not { Length: > 0 })
        {
            return ConversationErrors.ChannelNotAvailable(
                "WhatsApp is not available on this deployment - no App-level webhook secret/verify token is configured.")
                .ToProblem(httpContext);
        }

        if (string.IsNullOrWhiteSpace(request.PhoneNumberId))
        {
            return ConversationErrors.ChannelInvalidToken("A WhatsApp phone number id is required.").ToProblem(httpContext);
        }

        var user = httpContext.User;
        var site = new SiteId(siteId);

        WhatsAppPhoneNumberInfo phoneNumberInfo;
        try
        {
            phoneNumberInfo = await whatsAppApiClient.GetPhoneNumberAsync(request.Token, request.PhoneNumberId, cancellationToken);
        }
        catch (WhatsAppApiCallException ex)
        {
            return ConversationErrors.ChannelInvalidToken(ex.Message).ToProblem(httpContext);
        }

        var registered = await registerHandler.HandleAsync(
            new RegisterChannelCredential(
                user.GetOperatorId(), site, ChannelKind.WhatsApp, request.Token, phoneNumberInfo.Id),
            cancellationToken);
        if (registered.IsFailure)
        {
            return registered.Error!.Value.ToProblem(httpContext);
        }

        return Results.Created(
            $"/api/v1/sites/{siteId}/channels/whatsapp/{registered.Value.ChannelCredentialId.Value}",
            new ConnectWhatsAppChannelResponse(registered.Value.ChannelCredentialId.Value, registered.Value.CreatedAt));
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

    public sealed record ConnectWhatsAppChannelRequest(string Token, string PhoneNumberId);

    public sealed record ConnectWhatsAppChannelResponse(Guid ChannelCredentialId, DateTimeOffset CreatedAt);
}
