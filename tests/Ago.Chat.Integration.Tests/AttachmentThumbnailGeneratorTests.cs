using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Worker;
using Ago.Platform.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `5-04`'s thumbnail generation itself, real Postgres and real MinIO (`AttachmentFixture`) - the
/// RabbitMQ-driven trigger (`AttachmentThumbnailConsumer` reacting to a real `AttachmentConfirmed`)
/// is proven separately in <c>AttachmentThumbnailEndToEndTests</c>; this is the download-resize-
/// upload-persist work itself, called directly.
/// </summary>
[Collection(AttachmentCollection.Name)]
public sealed class AttachmentThumbnailGeneratorTests(AttachmentFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GenerateAsync_ForARealImage_UploadsAThumbnail_AndSetsThumbnailKey()
    {
        var (attachmentId, objectKey) = await SeedReadyImageAsync();

        await CreateGenerator().GenerateAsync(attachmentId, objectKey, CancellationToken.None);

        await using var db = fixture.CreateDbContext();
        var attachment = await db.Attachments.SingleAsync(a => a.Id == attachmentId);
        Assert.NotNull(attachment.ThumbnailKey);

        var thumbnailMetadata = await fixture.FileStorage.GetMetadataAsync(new ObjectKey(attachment.ThumbnailKey!), CancellationToken.None);
        Assert.NotNull(thumbnailMetadata);
        Assert.Equal("image/jpeg", thumbnailMetadata.ContentType);
    }

    [Fact]
    public async Task GenerateAsync_TheThumbnailFitsWithinTheConfiguredBounds()
    {
        var (attachmentId, objectKey) = await SeedReadyImageAsync(width: 2000, height: 1000);

        var generator = CreateGenerator(maxWidth: 256, maxHeight: 256);
        await generator.GenerateAsync(attachmentId, objectKey, CancellationToken.None);

        await using var db = fixture.CreateDbContext();
        var attachment = await db.Attachments.SingleAsync(a => a.Id == attachmentId);
        var downloadUrl = await fixture.FileStorage.CreateDownloadUrlAsync(
            new ObjectKey(attachment.ThumbnailKey!), TimeSpan.FromMinutes(5), CancellationToken.None);
        using var http = new HttpClient();
        var bytes = await http.GetByteArrayAsync(downloadUrl);
        using var thumbnail = SKBitmap.Decode(bytes);

        Assert.True(thumbnail.Width <= 256);
        Assert.True(thumbnail.Height <= 256);
        // Original is 2:1 - the resize must preserve that ratio, not stretch to a 256x256 square.
        Assert.Equal(256, thumbnail.Width);
        Assert.Equal(128, thumbnail.Height);
    }

    /// <summary>`5-04`'s Done-when: redelivering the same event twice produces exactly one thumbnail,
    /// not two, and no error.</summary>
    [Fact]
    public async Task GenerateAsync_CalledTwiceForTheSameAttachment_IsIdempotent()
    {
        var (attachmentId, objectKey) = await SeedReadyImageAsync();
        var generator = CreateGenerator();

        await generator.GenerateAsync(attachmentId, objectKey, CancellationToken.None);
        await using var afterFirst = fixture.CreateDbContext();
        var firstThumbnailKey = (await afterFirst.Attachments.SingleAsync(a => a.Id == attachmentId)).ThumbnailKey;

        await generator.GenerateAsync(attachmentId, objectKey, CancellationToken.None); // no throw

        await using var afterSecond = fixture.CreateDbContext();
        var secondThumbnailKey = (await afterSecond.Attachments.SingleAsync(a => a.Id == attachmentId)).ThumbnailKey;
        Assert.Equal(firstThumbnailKey, secondThumbnailKey);
    }

    [Fact]
    public async Task GenerateAsync_ForAnAttachmentThatNoLongerExists_DoesNotThrow()
    {
        var missingId = new AttachmentId(Guid.NewGuid());

        await CreateGenerator().GenerateAsync(missingId, "site/x/conv/y/z.png", CancellationToken.None);
    }

    private AttachmentThumbnailGenerator CreateGenerator(int maxWidth = 256, int maxHeight = 256) => new(
        new AttachmentRepository(fixture.CreateDbContext()),
        fixture.FileStorage,
        Options.Create(new AttachmentThumbnailOptions { MaxWidth = maxWidth, MaxHeight = maxHeight, JpegQuality = 80 }),
        NullLogger<AttachmentThumbnailGenerator>.Instance);

    private async Task<(AttachmentId Id, string ObjectKey)> SeedReadyImageAsync(int width = 800, int height = 600)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        var objectKey = $"site/{siteId.Value}/conv/{conversationId.Value}/{Guid.NewGuid():N}.png";
        var imageBytes = CreateTestPngBytes(width, height);

        var presigned = await fixture.FileStorage.CreateUploadAsync(
            new ObjectKey(objectKey), new UploadConstraints("image/png", imageBytes.Length, TimeSpan.FromMinutes(5)), CancellationToken.None);
        using (var http = new HttpClient())
        using (var content = new ByteArrayContent(imageBytes))
        {
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            var response = await http.PutAsync(presigned.Url, content);
            response.EnsureSuccessStatusCode();
        }

        var attachment = Attachment.CreatePending(
            new AttachmentId(Guid.NewGuid()), siteId, conversationId, objectKey, "image/png", imageBytes.Length, Now);
        attachment.ConfirmReady(imageBytes.Length, "image/png", Now);

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(visitorId, siteId, Now));
        db.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, Now));
        db.Attachments.Add(attachment);
        await db.SaveChangesAsync();

        return (attachment.Id, objectKey);
    }

    private static byte[] CreateTestPngBytes(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
