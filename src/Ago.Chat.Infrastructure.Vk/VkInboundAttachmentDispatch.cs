using System.Net.Http.Headers;
using Ago.Chat.Application.UseCases.ReceiveChannelAttachment;
using Ago.Chat.Domain;
using Microsoft.Extensions.Logging;

namespace Ago.Chat.Infrastructure.Vk;

/// <summary>
/// `25-166`: the one place a <see cref="ParsedVkImage"/> becomes a real AGO Chat attachment - mirrors
/// <c>Ago.Chat.Infrastructure.MaxBot.MaxInboundAttachmentDispatch</c> exactly, and needs no per-request
/// credential at all: <see cref="VkApiClient.DownloadImageAsync"/>'s own remarks explain why VK's own
/// photo URL is directly fetchable, unlike MAX's outbound-token-free-but-still-per-request shape or
/// Telegram's/WhatsApp's own authenticated download steps. VK has only one inbound mechanism at all
/// (<c>Ago.Chat.Api.Channels.VkWebhookEndpoints</c> - <c>VkChannelAdapter</c>'s own remarks on why there
/// is no polling sibling), unlike MAX's webhook/long-poll pair, so unlike
/// <c>MaxInboundAttachmentDispatch</c> this type has exactly one caller.
///
/// <para><b>Why the presigned-URL upload happens here, in Infrastructure, and not inside
/// <see cref="ReceiveChannelAttachmentHandler"/>.</b> That handler's own remarks give the full
/// reasoning, identical for every channel: <c>IFileStorage</c> is presign-only by design (`adr/0008`),
/// and CLAUDE.md rule 2 forbids Application any <c>HttpClient</c> of its own. This class plays the
/// "browser" role a widget visitor's own upload would otherwise play, bracketing one bare
/// <c>HttpClient PUT</c> between <see cref="ReceiveChannelAttachmentHandler.PrepareAsync"/> (grant
/// check, budget, presign) and <see cref="ReceiveChannelAttachmentHandler.CompleteAsync"/> (HEAD-verify,
/// send).</para>
///
/// <para>A static field for the upload <see cref="HttpClient"/>, matching
/// <c>MaxInboundAttachmentDispatch</c>'s own precedent exactly: this PUT always targets a short-lived,
/// presigned URL this process itself just minted, never VK's own API or CDN, or any other long-lived
/// endpoint that would want the pooling/rotation <c>IHttpClientFactory</c> exists for.</para>
/// </summary>
public static class VkInboundAttachmentDispatch
{
    private static readonly HttpClient UploadHttp = new();

    public static async Task DispatchImageAsync(
        ReceiveChannelAttachmentHandler handler,
        VkApiClient client,
        ILogger logger,
        SiteId siteId,
        ParsedVkImage image,
        ExternalChannelAddress sender,
        ExternalMessageId externalMessageId,
        CancellationToken cancellationToken)
    {
        var download = await client.DownloadImageAsync(image.Url, cancellationToken);
        if (download is null)
        {
            logger.LogWarning(
                "Could not download a VK image attachment for site {SiteId}: the URL VK supplied was refused or unusable.",
                siteId.Value);
            return;
        }

        var prepared = await handler.PrepareAsync(
            new PrepareChannelAttachmentUpload(siteId, ChannelKind.Vk, sender, download.ContentType, download.Content.Length),
            cancellationToken);

        if (prepared.IsFailure)
        {
            logger.LogWarning(
                "Could not prepare a VK image attachment for site {SiteId}: {Code} {Message}",
                siteId.Value, prepared.Error!.Value.Code, prepared.Error!.Value.Message);
            return;
        }

        if (prepared.Value.Outcome == ChannelAttachmentPrepareOutcome.Refused)
        {
            // ReceiveChannelAttachmentHandler.PrepareAsync already told the visitor why, over this same
            // channel - see its own remarks. Nothing left to do here.
            return;
        }

        using var uploadContent = new ByteArrayContent(download.Content);
        uploadContent.Headers.ContentType = new MediaTypeHeaderValue(download.ContentType);

        using var uploadResponse = await UploadHttp.PutAsync(prepared.Value.UploadUrl, uploadContent, cancellationToken);
        if (!uploadResponse.IsSuccessStatusCode)
        {
            // A presigned PUT this process itself just minted being refused by storage is not an
            // ordinary provider refusal the way VkApiClient's own terminal/transient split models one -
            // see MaxInboundAttachmentDispatch's own remarks for the identical reasoning: logged and
            // dropped, not thrown, since retrying this exact update would presign a fresh URL anyway.
            logger.LogWarning(
                "Uploading a VK image attachment failed for site {SiteId}: storage returned {StatusCode}.",
                siteId.Value, (int)uploadResponse.StatusCode);
            return;
        }

        var completed = await handler.CompleteAsync(
            new CompleteChannelAttachmentUpload(
                prepared.Value.AttachmentId!.Value, prepared.Value.VisitorId!.Value, prepared.Value.ConversationId!.Value,
                externalMessageId, ChannelKind.Vk),
            cancellationToken);

        if (completed.IsFailure)
        {
            logger.LogWarning(
                "Could not complete a VK image attachment for site {SiteId}: {Code} {Message}",
                siteId.Value, completed.Error!.Value.Code, completed.Error!.Value.Message);
        }
    }
}
