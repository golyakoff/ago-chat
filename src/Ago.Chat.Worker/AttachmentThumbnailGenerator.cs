using System.Net.Http.Headers;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Ago.Chat.Worker;

/// <summary>
/// `5-04`: the actual thumbnailing work - download, resize, upload, persist. Downloads and uploads go
/// through a presigned URL and a bare <see cref="HttpClient"/>, exactly the way a browser would
/// (`S3FileStorageTests`' own precedent) - <see cref="IFileStorage"/> is presign-only by design
/// (`adr/0008`), and this is the Worker calling it the same way any other consumer of the port does,
/// never a raw <c>IAmazonS3</c> call (`file-storage.md`'s own words for this job).
///
/// Idempotency (`5-04`'s Done-when): a redelivered event for an attachment that already has a
/// <see cref="Attachment.ThumbnailKey"/> is a no-op. This check is a plain read-then-write, not a
/// database-atomic guard - safe because RabbitMQ's `Competing` subscription mode only ever redelivers
/// a message *after* the previous attempt's outcome (ack/nack) is known, never delivers the same
/// message to two consumers simultaneously; a genuinely concurrent double-delivery is not a case this
/// needs to defend against.
/// </summary>
public sealed class AttachmentThumbnailGenerator(
    IAttachmentRepository attachments,
    IFileStorage fileStorage,
    IOptions<AttachmentThumbnailOptions> options,
    ILogger<AttachmentThumbnailGenerator> logger)
{
    // Ephemeral, immediately-consumed URLs for the Worker's own internal transfer - unrelated to
    // AttachmentOptions.UploadLifetime/DownloadLifetime, which govern client-facing presigns.
    private static readonly TimeSpan UrlLifetime = TimeSpan.FromMinutes(2);
    private static readonly HttpClient Http = new();

    public async Task GenerateAsync(AttachmentId attachmentId, string objectKey, CancellationToken cancellationToken)
    {
        var attachment = await attachments.GetByIdAsync(attachmentId, cancellationToken);
        if (attachment is null)
        {
            logger.LogWarning(
                "AttachmentConfirmed for {AttachmentId} but the row no longer exists; skipping thumbnail.", attachmentId.Value);
            return;
        }

        if (attachment.ThumbnailKey is not null)
        {
            return;
        }

        var downloadUrl = await fileStorage.CreateDownloadUrlAsync(new ObjectKey(objectKey), UrlLifetime, cancellationToken);
        using var downloadResponse = await Http.GetAsync(downloadUrl, cancellationToken);
        downloadResponse.EnsureSuccessStatusCode();
        var originalBytes = await downloadResponse.Content.ReadAsByteArrayAsync(cancellationToken);

        var thumbnailBytes = Resize(originalBytes, options.Value.MaxWidth, options.Value.MaxHeight, options.Value.JpegQuality);
        var thumbnailKey = DeriveThumbnailKey(objectKey);

        var upload = await fileStorage.CreateUploadAsync(
            new ObjectKey(thumbnailKey), new UploadConstraints("image/jpeg", thumbnailBytes.Length, UrlLifetime), cancellationToken);
        using var uploadContent = new ByteArrayContent(thumbnailBytes);
        uploadContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        using var uploadResponse = await Http.PutAsync(upload.Url, uploadContent, cancellationToken);
        uploadResponse.EnsureSuccessStatusCode();

        attachment.SetThumbnail(thumbnailKey);
        await attachments.SaveAsync(attachment, cancellationToken);
    }

    // site/{site}/conv/{conv}/{uuid7}.png -> site/{site}/conv/{conv}/{uuid7}_thumb.jpg - always a
    // distinct object from the original, so a thumbnail can never collide with (or overwrite) it.
    private static string DeriveThumbnailKey(string objectKey)
    {
        var extension = Path.GetExtension(objectKey);
        var withoutExtension = extension.Length > 0 ? objectKey[..^extension.Length] : objectKey;
        return $"{withoutExtension}_thumb.jpg";
    }

    private static byte[] Resize(byte[] originalBytes, int maxWidth, int maxHeight, int quality)
    {
        using var original = SKBitmap.Decode(originalBytes)
            ?? throw new InvalidOperationException("Could not decode the source image for thumbnailing.");

        // Never upscale an original already smaller than the thumbnail bounds.
        var scale = Math.Min(1.0, Math.Min((double)maxWidth / original.Width, (double)maxHeight / original.Height));
        var width = Math.Max(1, (int)(original.Width * scale));
        var height = Math.Max(1, (int)(original.Height * scale));

        using var resized = original.Resize(new SKImageInfo(width, height), SKSamplingOptions.Default);
        using var image = SKImage.FromBitmap(resized);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }
}
