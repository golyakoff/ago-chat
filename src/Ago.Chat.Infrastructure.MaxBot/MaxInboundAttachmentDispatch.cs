using System.Net.Http.Headers;
using Ago.Chat.Application.UseCases.ReceiveChannelAttachment;
using Ago.Chat.Domain;
using Microsoft.Extensions.Logging;

namespace Ago.Chat.Infrastructure.MaxBot;

/// <summary>
/// `25-161`: the one place a <see cref="ParsedMaxImage"/> becomes a real AGO Chat attachment - shared by
/// both of MAX's inbound mechanisms (<c>MaxWebhookEndpoints</c>/<see cref="MaxLongPollingService"/>) for
/// the identical "one dispatch, however many mechanisms feed it" reason
/// <see cref="MaxInboundMessageParser"/> is already shared between them.
///
/// <para><b>Why the presigned-URL upload happens here, in Infrastructure, and not inside
/// <see cref="ReceiveChannelAttachmentHandler"/>.</b> That handler's own remarks give the full
/// reasoning: <c>IFileStorage</c> is presign-only by design (`adr/0008`), and CLAUDE.md rule 2 forbids
/// Application any <c>HttpClient</c> of its own. This class is what plays the "browser" role a widget
/// visitor's own upload would otherwise play, bracketing one bare <c>HttpClient PUT</c> between
/// <see cref="ReceiveChannelAttachmentHandler.PrepareAsync"/> (grant check, budget, presign) and
/// <see cref="ReceiveChannelAttachmentHandler.CompleteAsync"/> (HEAD-verify, send) - the same shape
/// <c>Ago.Chat.Worker.AttachmentThumbnailGenerator</c> already established for a different
/// server-initiated upload.</para>
///
/// <para>A static field for the upload <see cref="HttpClient"/>, matching
/// <c>AttachmentThumbnailGenerator</c>'s own precedent exactly: this PUT always targets a
/// short-lived, presigned URL this process itself just minted, never MAX's own API or any other
/// long-lived endpoint that would want the pooling/rotation <c>IHttpClientFactory</c> exists for.</para>
/// </summary>
public static class MaxInboundAttachmentDispatch
{
    private static readonly HttpClient UploadHttp = new();

    public static async Task DispatchImageAsync(
        ReceiveChannelAttachmentHandler handler,
        MaxApiClient client,
        ILogger logger,
        SiteId siteId,
        ParsedMaxImage image,
        ExternalChannelAddress sender,
        ExternalMessageId externalMessageId,
        CancellationToken cancellationToken)
    {
        var download = await client.DownloadImageAsync(image.Url, cancellationToken);
        if (download is null)
        {
            logger.LogWarning(
                "Could not download a MAX image attachment for site {SiteId}: the URL MAX supplied was refused or unusable.",
                siteId.Value);
            return;
        }

        var prepared = await handler.PrepareAsync(
            new PrepareChannelAttachmentUpload(siteId, ChannelKind.Max, sender, download.ContentType, download.Content.Length),
            cancellationToken);

        if (prepared.IsFailure)
        {
            logger.LogWarning(
                "Could not prepare a MAX image attachment for site {SiteId}: {Code} {Message}",
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
            // ordinary provider refusal the way MaxApiClient's own terminal/transient split models one -
            // it means the declared size/content-type this method read off MAX's own response no longer
            // matches what storage actually received, or the URL's short lifetime already expired.
            // Logged and dropped, not thrown: retrying this exact update would presign a fresh URL
            // anyway (a new AttachmentId, per `23-75`'s own conversation-budget accounting), so nothing
            // about this failure is fixed by an exception unwinding to the same catch-and-backoff every
            // other transient failure on this path already gets.
            logger.LogWarning(
                "Uploading a MAX image attachment failed for site {SiteId}: storage returned {StatusCode}.",
                siteId.Value, (int)uploadResponse.StatusCode);
            return;
        }

        var completed = await handler.CompleteAsync(
            new CompleteChannelAttachmentUpload(
                prepared.Value.AttachmentId!.Value, prepared.Value.VisitorId!.Value, prepared.Value.ConversationId!.Value,
                externalMessageId, ChannelKind.Max),
            cancellationToken);

        if (completed.IsFailure)
        {
            logger.LogWarning(
                "Could not complete a MAX image attachment for site {SiteId}: {Code} {Message}",
                siteId.Value, completed.Error!.Value.Code, completed.Error!.Value.Message);
        }
    }
}
