using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Worker;
using Ago.Platform.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `23-76`'s Done-when: "the same bytes uploaded repeatedly cost one object, within a tenant" - real
/// Postgres and real MinIO (`AttachmentFixture`, the identical fixture
/// <see cref="AttachmentThumbnailGeneratorTests"/> already uses for the sibling Worker job), never
/// mocked: a real object actually gets deleted from MinIO, a real site-budget reservation actually
/// gets released, proven by reading both back afterwards rather than asserting a call happened.
/// </summary>
[Collection(AttachmentCollection.Name)]
public sealed class AttachmentDeduplicatorTests(AttachmentFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Body = "the exact same bytes, twice"u8.ToArray();

    [Fact]
    public async Task DeduplicateAsync_FirstCopyOfItsBytes_JustRecordsTheHash_KeepsItsOwnObject()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var (attachmentId, objectKey) = await SeedReadyAttachmentAsync(siteId, Body);

        await CreateDeduplicator().DeduplicateAsync(attachmentId, objectKey, CancellationToken.None);

        await using var db = fixture.CreateDbContext();
        var attachment = await db.Attachments.SingleAsync(a => a.Id == attachmentId);
        Assert.NotNull(attachment.ContentHash);
        Assert.Equal(objectKey, attachment.ObjectKey);
        // Its own object is still there - nothing to reclaim for a first copy.
        Assert.NotNull(await fixture.FileStorage.GetMetadataAsync(new ObjectKey(objectKey), CancellationToken.None));
    }

    /// <summary>The actual claim under test: a second upload of the identical bytes, within the same
    /// tenant, ends with one surviving object, not two, and the tenant's storage reservation for the
    /// redundant bytes is genuinely released - read back from real Postgres, not asserted as a call.
    /// </summary>
    [Fact]
    public async Task DeduplicateAsync_SecondUploadOfIdenticalBytes_RepointsToTheFirstObject_DeletesItsOwn_AndReleasesTheSiteReservation()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await SeedSiteAsync(siteId);
        var siteBudget = new SiteAttachmentStorageBudgetStore(fixture.CreateDbContext());

        var (firstId, firstKey) = await SeedReadyAttachmentAsync(siteId, Body);
        await CreateDeduplicator(siteBudget).DeduplicateAsync(firstId, firstKey, CancellationToken.None);

        var (secondId, secondKey) = await SeedReadyAttachmentAsync(siteId, Body);
        // The reservation CreateAttachmentHandler would have made for the second upload's own bytes -
        // built here directly since this test drives AttachmentDeduplicator standalone, the same
        // "prove the real mechanics, not a call happened" reasoning this file's own remarks state.
        var reservation = await siteBudget.TryReserveAsync(siteId, Body.Length, 10_000, CancellationToken.None);
        Assert.True(reservation.Reserved);

        await CreateDeduplicator(siteBudget).DeduplicateAsync(secondId, secondKey, CancellationToken.None);

        await using var db = fixture.CreateDbContext();
        var second = await db.Attachments.SingleAsync(a => a.Id == secondId);
        Assert.Equal(firstKey, second.ObjectKey);
        Assert.NotNull(second.ContentHash);

        var first = await db.Attachments.SingleAsync(a => a.Id == firstId);
        Assert.Equal(second.ContentHash, first.ContentHash);

        // The second upload's own object is gone - "cost one object," not two, at rest in MinIO.
        Assert.Null(await fixture.FileStorage.GetMetadataAsync(new ObjectKey(secondKey), CancellationToken.None));
        // The first object survives - the canonical copy both rows now point at.
        Assert.NotNull(await fixture.FileStorage.GetMetadataAsync(new ObjectKey(firstKey), CancellationToken.None));

        // The site's reservation for the redundant bytes is released - the tenant is not charged twice
        // for storing the same content, read directly from the real column rather than inferred from
        // a second TryReserveAsync call (which would happen to succeed at the exact boundary either
        // way and prove nothing).
        Assert.Equal(0, await ReadReservedBytesAsync(siteId));
    }

    /// <summary>Redelivery guard, the identical shape `AttachmentThumbnailGenerator`'s own
    /// `ThumbnailKey is not null` check gives - a second call for the same, already-deduplicated
    /// attachment is a no-op, not a second lookup or a second release.</summary>
    [Fact]
    public async Task DeduplicateAsync_CalledTwiceForTheSameAttachment_IsIdempotent()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var (attachmentId, objectKey) = await SeedReadyAttachmentAsync(siteId, Body);
        var deduplicator = CreateDeduplicator();

        await deduplicator.DeduplicateAsync(attachmentId, objectKey, CancellationToken.None);
        await using var afterFirst = fixture.CreateDbContext();
        var firstHash = (await afterFirst.Attachments.SingleAsync(a => a.Id == attachmentId)).ContentHash;

        await deduplicator.DeduplicateAsync(attachmentId, objectKey, CancellationToken.None); // no throw

        await using var afterSecond = fixture.CreateDbContext();
        var secondHash = (await afterSecond.Attachments.SingleAsync(a => a.Id == attachmentId)).ContentHash;
        Assert.Equal(firstHash, secondHash);
    }

    [Fact]
    public async Task DeduplicateAsync_ForAnAttachmentThatNoLongerExists_DoesNotThrow()
    {
        var missingId = new AttachmentId(Guid.NewGuid());

        await CreateDeduplicator().DeduplicateAsync(missingId, "site/x/conv/y/z.png", CancellationToken.None);
    }

    private AttachmentDeduplicator CreateDeduplicator(ISiteAttachmentStorageBudget? siteBudget = null) => new(
        new AttachmentRepository(fixture.CreateDbContext()),
        siteBudget ?? new SiteAttachmentStorageBudgetStore(fixture.CreateDbContext()),
        fixture.FileStorage,
        NullLogger<AttachmentDeduplicator>.Instance);

    private async Task SeedSiteAsync(SiteId siteId)
    {
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        await db.SaveChangesAsync();
    }

    private async Task<(AttachmentId Id, string ObjectKey)> SeedReadyAttachmentAsync(SiteId siteId, byte[] body)
    {
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        var objectKey = $"site/{siteId.Value}/conv/{conversationId.Value}/{Guid.NewGuid():N}.png";

        var presigned = await fixture.FileStorage.CreateUploadAsync(
            new ObjectKey(objectKey), new UploadConstraints("image/png", body.Length, TimeSpan.FromMinutes(5)), CancellationToken.None);
        using (var http = new HttpClient())
        using (var content = new ByteArrayContent(body))
        {
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            var response = await http.PutAsync(presigned.Url, content);
            response.EnsureSuccessStatusCode();
        }

        var attachment = Attachment.CreatePending(
            new AttachmentId(Guid.NewGuid()), siteId, conversationId, objectKey, "image/png", body.Length, Now);
        attachment.ConfirmReady(body.Length, "image/png", Now);

        await using var db = fixture.CreateDbContext();
        if (!await db.Sites.AnyAsync(s => s.Id == siteId))
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        }

        db.Visitors.Add(new Visitor(visitorId, siteId, Now));
        db.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, Now));
        db.Attachments.Add(attachment);
        await db.SaveChangesAsync();

        return (attachment.Id, objectKey);
    }

    private async Task<long> ReadReservedBytesAsync(SiteId siteId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT attachment_bytes_reserved FROM sites WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", siteId.Value);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
