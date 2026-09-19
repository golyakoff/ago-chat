using System.Text.Json;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.ReceiveChannelAttachment;
using Ago.Chat.Application.UseCases.ReceiveChannelMessage;
using Ago.Chat.Api.Http;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Vk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ago.Chat.Api.Channels;

/// <summary>
/// `14-08`: VK's own production inbound mechanism, and its <em>only</em> one (this channel has no
/// long-polling sibling - <c>VkChannelAdapter</c>'s own remarks explain why). Placed in
/// <c>Ago.Chat.Api</c> rather than <c>Ago.Chat.Webhooks</c>, the identical reasoning
/// <c>MaxWebhookEndpoints</c>' own remarks give: `adr/0013` made <c>Ago.Chat.Webhooks</c> a bulkhead for
/// <em>outbound</em> calls to a shop's own CRM, and an inbound VK delivery has the opposite failure
/// profile - a request <em>we</em> must answer quickly, doing bounded local work, request-shaped rather
/// than third-party-latency-shaped.
///
/// <para><b>The confirmation handshake - this item's own hardest requirement, and the one place a wrong
/// answer means VK never delivers a single real event.</b> VK's Callback API sends a
/// <c>{"type":"confirmation","group_id":...,"secret":...}</c> event exactly once per callback-URL
/// registration attempt (and again on demand, whenever a community admin re-triggers verification from
/// VK's own settings UI) and expects the raw confirmation string back as the <em>entire response
/// body</em> - no JSON, no wrapping, plain text, HTTP 200. This handler fetches that string live, via
/// <see cref="VkApiClient.GetCallbackConfirmationCodeAsync"/>, rather than asking the shop to copy it out
/// of VK's own settings page into this system - see that method's own remarks for why VK's API makes
/// that unnecessary. Every other accepted event (<see cref="VkCallbackEventTypes.MessageNew"/>, and
/// anything this item does not understand) must answer with the literal text <c>"ok"</c>, not an empty
/// 200 - confirmed from VK's own SDK's Callback API server handler, whose <c>messageNew</c>/default
/// handlers both <c>echo 'ok'</c> rather than returning nothing. A non-200, a wrong confirmation string,
/// or an empty body where <c>"ok"</c> was expected are all indistinguishable to VK from "this endpoint
/// does not work", and VK will retry and then disable the callback rather than tell anyone why.</para>
///
/// <para><b>Authentication.</b> The same <c>{credentialId}</c>-path-plus-secret shape
/// <c>MaxWebhookEndpoints</c> already established: VK's own Callback API settings let a community admin
/// set a <c>Secret key</c>, echoed back on every delivery (confirmation included) as the event body's own
/// <c>secret</c> field - the mirror of `6-03`'s outbound <c>X-Ago-Signature</c> scheme, and the exact
/// value <c>RegisterChannelCredentialHandler</c> already generates for every channel
/// (<c>ChannelCredential.MatchesWebhookSecret</c>'s own constant-time check). The <c>{credentialId}</c>
/// segment is routing, not authentication on its own - a request whose secret does not match this
/// specific credential is rejected regardless of how the id was obtained, the identical info-hiding shape
/// <c>MaxWebhookEndpoints</c>' own remarks state.</para>
///
/// <para><b>`25-166`: an inbound photo, dispatched to <see cref="VkInboundAttachmentDispatch"/> rather
/// than <see cref="ReceiveChannelMessageHandler"/>.</b> The same reasoning <c>MaxWebhookEndpoints</c>'s
/// own remarks already state for their own channel: routing through the shared, channel-agnostic
/// <see cref="ReceiveChannelAttachmentHandler"/> means the `23-78` upload grant, rate limits and storage
/// budgets all apply to a VK-sourced image identically to a widget-sourced one, with no second copy of
/// any of those rules.</para>
/// </summary>
public static class VkWebhookEndpoints
{
    private const string OkResponseBody = "ok";

    public static void MapVkWebhookEndpoints(this WebApplication app)
    {
        app.MapPost("/webhooks/vk/{credentialId:guid}", HandleAsync);
    }

    private static async Task<IResult> HandleAsync(
        Guid credentialId,
        HttpContext httpContext,
        IChannelCredentialRepository credentials,
        IChannelCredentialCipher cipher,
        VkApiClient vkApiClient,
        ReceiveChannelMessageHandler receiveHandler,
        ReceiveChannelAttachmentHandler receiveAttachmentHandler,
        ILogger<ReceiveChannelAttachmentHandler> logger,
        CancellationToken cancellationToken)
    {
        var credential = await credentials.GetByIdAsync(new ChannelCredentialId(credentialId), cancellationToken);
        if (credential is null || !credential.Active)
        {
            // A missing id and a revoked one read identically - MaxWebhookEndpoints' own info-hiding
            // shape, so a revoked credential's own URL can never be used to fingerprint which tenants
            // exist or ever connected VK.
            return Results.NotFound();
        }

        VkCallbackEvent? callbackEvent;
        try
        {
            callbackEvent = await JsonSerializer.DeserializeAsync<VkCallbackEvent>(
                httpContext.Request.Body, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return Results.BadRequest();
        }

        if (callbackEvent is null)
        {
            return Results.BadRequest();
        }

        var secret = callbackEvent.Secret;
        if (string.IsNullOrEmpty(secret) || !credential.MatchesWebhookSecret(secret))
        {
            return Results.Unauthorized();
        }

        if (callbackEvent.Type == VkCallbackEventTypes.Confirmation)
        {
            if (credential.ProviderAccountId is not { Length: > 0 } providerAccountId || !long.TryParse(providerAccountId, out var groupId))
            {
                // Should not happen - every VK credential this system creates populates
                // ProviderAccountId at registration (VkChannelAdapter's own remarks on this
                // inconsistency).
                throw new InvalidOperationException(
                    $"VK channel credential {credential.Id.Value} has no usable ProviderAccountId (community id).");
            }

            var token = cipher.Decrypt(credential.TokenCiphertext);
            var code = await vkApiClient.GetCallbackConfirmationCodeAsync(token, groupId, cancellationToken);
            return Results.Text(code);
        }

        // VK retries a non-2xx delivery and eventually disables the callback (this endpoint's own
        // remarks) - an event this deserialized fine but this item has no use case for (any type other
        // than message_new, or a message_new this parser refuses - VkInboundMessageParser's own remarks
        // on message.out) is acknowledged with "ok" rather than rejected, so VK does not burn its retry
        // budget resending something that will never parse differently.
        var parsed = VkInboundMessageParser.TryParse(callbackEvent);
        if (parsed is null)
        {
            return Results.Text(OkResponseBody);
        }

        if (!string.IsNullOrWhiteSpace(parsed.Text))
        {
            var result = await receiveHandler.HandleAsync(
                new ReceiveChannelMessage(
                    credential.SiteId,
                    ChannelKind.Vk,
                    new ExternalChannelAddress(parsed.PeerId.ToString()),
                    new ExternalMessageId(parsed.ExternalMessageId),
                    parsed.Text),
                cancellationToken);

            if (result.IsFailure)
            {
                return result.Error!.Value.ToProblem(httpContext);
            }
        }

        // `25-166`: a sent photo, dispatched the same way a captioned MAX/Telegram photo already is -
        // its own sibling command, never folded into ReceiveChannelMessage above. See
        // VkInboundAttachmentDispatch's own remarks for the full download-prepare-upload-complete
        // protocol this one call hides. Never turned into a non-"ok" response for its own internal
        // failures, matching MaxWebhookEndpoints' own identical branch: this is downstream of "the event
        // parsed fine," the same territory the text branch's own `result.IsFailure` check above is
        // reserved for.
        if (parsed.Image is { } image)
        {
            await VkInboundAttachmentDispatch.DispatchImageAsync(
                receiveAttachmentHandler, vkApiClient, logger, credential.SiteId, image,
                new ExternalChannelAddress(parsed.PeerId.ToString()),
                new ExternalMessageId(parsed.ExternalMessageId),
                cancellationToken);
        }

        return Results.Text(OkResponseBody);
    }
}
