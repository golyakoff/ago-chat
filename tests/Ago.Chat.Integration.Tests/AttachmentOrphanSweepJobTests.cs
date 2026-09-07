using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Worker;
using Ago.Platform.Abstractions;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `5-04`'s orphan sweep, real Postgres and real MinIO (`AttachmentFixture`, shared with the upload
/// flow tests - no RabbitMQ needed here, the sweep never touches the outbox).
/// </summary>
[Collection(AttachmentCollection.Name)]
public sealed class AttachmentOrphanSweepJobTests(AttachmentFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan UploadLifetime = TimeSpan.FromMinutes(10);

    [Fact]
    public async Task SweepAsync_DeletesAnExpiredPendingAttachment_RowAndStorageObject()
    {
        var (attachmentId, objectKey) = await SeedAsync(AttachmentState.Pending, Now - UploadLifetime - TimeSpan.FromMinutes(1));
        await UploadRealObjectAsync(objectKey);

        await CreateJob().SweepAsync(CancellationToken.None);

        await using var db = fixture.CreateDbContext();
        Assert.False(await db.Attachments.AnyAsync(a => a.Id == attachmentId));
        var metadata = await fixture.FileStorage.GetMetadataAsync(new ObjectKey(objectKey), CancellationToken.None);
        Assert.Null(metadata);
    }

    [Fact]
    public async Task SweepAsync_LeavesAPendingAttachmentYoungerThanTheUploadLifetimeAlone()
    {
        var (attachmentId, _) = await SeedAsync(AttachmentState.Pending, Now - TimeSpan.FromMinutes(1));

        await CreateJob().SweepAsync(CancellationToken.None);

        await using var db = fixture.CreateDbContext();
        Assert.True(await db.Attachments.AnyAsync(a => a.Id == attachmentId));
    }

    [Fact]
    public async Task SweepAsync_LeavesAReadyAttachmentAlone_RegardlessOfAge()
    {
        var (attachmentId, _) = await SeedAsync(AttachmentState.Ready, Now - UploadLifetime - TimeSpan.FromDays(1));

        await CreateJob().SweepAsync(CancellationToken.None);

        await using var db = fixture.CreateDbContext();
        Assert.True(await db.Attachments.AnyAsync(a => a.Id == attachmentId));
    }

    /// <summary>
    /// `5-04`'s Done-when: "an attachment confirmed during a sweep tick... is not deleted." Built the
    /// same way `WaitingConversationClaimQueryTests` proves `SKIP LOCKED` - two real, manually
    /// sequenced transactions, not a hope-the-scheduler-races-them test. Transaction A holds the row
    /// locked (simulating a confirm's own in-flight `UPDATE`, not yet committed) while the sweep's
    /// claim query runs concurrently on a separate connection; `FOR UPDATE SKIP LOCKED` means the
    /// sweep skips the still-locked row outright rather than blocking on it, so it claims nothing.
    /// Only after transaction A commits (the "confirm" landing) does the row settle as `Ready` and
    /// permanently safe from any later sweep tick.
    /// </summary>
    [Fact]
    public async Task SweepAsync_SkipsARowLockedByAnInFlightConfirm_NeverDeletingIt()
    {
        var (attachmentId, objectKey) = await SeedAsync(AttachmentState.Pending, Now - UploadLifetime - TimeSpan.FromMinutes(1));
        await UploadRealObjectAsync(objectKey);

        await using var confirmingConnection = await fixture.DataSource.OpenConnectionAsync();
        await using var confirmingTransaction = await confirmingConnection.BeginTransactionAsync();
        await using (var command = new Npgsql.NpgsqlCommand(
            "UPDATE attachments SET state = 'Ready' WHERE id = @id", confirmingConnection, confirmingTransaction))
        {
            command.Parameters.AddWithValue("id", attachmentId.Value);
            await command.ExecuteNonQueryAsync();
        }
        // Deliberately not committed yet - this row is now locked, exactly like a confirm's own
        // UPDATE mid-transaction.

        var claimedWhileLocked = await AttachmentOrphanSweepQuery.ClaimExpiredPendingBatchAsync(
            await fixture.DataSource.OpenConnectionAsync(), Now, batchSize: 100, CancellationToken.None);
        Assert.DoesNotContain(claimedWhileLocked, c => c.Id == attachmentId);

        await confirmingTransaction.CommitAsync();

        // Even after the commit, later sweep ticks must never delete it - it is Ready now, not Pending.
        await CreateJob().SweepAsync(CancellationToken.None);

        await using var db = fixture.CreateDbContext();
        var reloaded = await db.Attachments.SingleAsync(a => a.Id == attachmentId);
        Assert.Equal(AttachmentState.Ready, reloaded.State);
    }

    /// <summary>
    /// `23-75`'s own Scope: "reserved at presign, released by the existing pending sweep." An unused
    /// slot's reservation must not outlive the row the sweep just deleted - proven here by reserving
    /// the attachment's own declared size against its conversation directly (bypassing
    /// <c>CreateAttachmentHandler</c>, the same "seed the row this test needs, not the whole flow that
    /// would normally produce it" shape <see cref="SeedAsync"/> already uses for the attachment
    /// itself), then confirming the sweep's own atomic statement brings the reservation back to zero
    /// in the same pass that deletes the row.
    /// </summary>
    [Fact]
    public async Task SweepAsync_ReleasesTheSweptAttachmentsConversationBudgetReservation()
    {
        const long declaredSizeBytes = 5; // matches UploadRealObjectAsync's own hardcoded "12345" body.
        var (attachmentId, objectKey, conversationId) = await SeedWithConversationIdAsync(
            AttachmentState.Pending, Now - UploadLifetime - TimeSpan.FromMinutes(1));
        await UploadRealObjectAsync(objectKey);

        await using (var db = fixture.CreateDbContext())
        {
            var reservation = await new ConversationAttachmentBudgetStore(db)
                .TryReserveAsync(conversationId, declaredSizeBytes, budgetBytes: declaredSizeBytes, CancellationToken.None);
            Assert.True(reservation.Reserved);
        }
        Assert.Equal(declaredSizeBytes, await ReadReservedBytesAsync(conversationId));

        await CreateJob().SweepAsync(CancellationToken.None);

        await using var verifyDb = fixture.CreateDbContext();
        Assert.False(await verifyDb.Attachments.AnyAsync(a => a.Id == attachmentId));
        Assert.Equal(0, await ReadReservedBytesAsync(conversationId));
    }

    private async Task<long> ReadReservedBytesAsync(ConversationId conversationId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT attachment_bytes_reserved FROM conversations WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", conversationId.Value);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private AttachmentOrphanSweepJob CreateJob() => new(
        fixture.DataSource,
        fixture.FileStorage,
        new FixedClock(Now),
        new Ago.Chat.Application.UseCases.CreateAttachment.AttachmentOptions { UploadLifetime = UploadLifetime },
        Options.Create(new AttachmentOrphanSweepJobOptions()),
        NullLogger<AttachmentOrphanSweepJob>.Instance);

    private async Task<(AttachmentId Id, string ObjectKey)> SeedAsync(AttachmentState state, DateTimeOffset createdAt)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        var objectKey = $"site/{siteId.Value}/conv/{conversationId.Value}/{Guid.NewGuid():N}.png";

        var attachment = Attachment.CreatePending(
            new AttachmentId(Guid.NewGuid()), siteId, conversationId, objectKey, "image/png", 5, createdAt);
        if (state == AttachmentState.Ready)
        {
            attachment.ConfirmReady(5, "image/png", createdAt);
        }

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(visitorId, siteId, createdAt));
        db.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, createdAt));
        db.Attachments.Add(attachment);
        await db.SaveChangesAsync();

        return (attachment.Id, objectKey);
    }

    /// <summary>The identical seed as <see cref="SeedAsync"/>, also handing back the conversation id -
    /// only <see cref="SweepAsync_ReleasesTheSweptAttachmentsConversationBudgetReservation"/> needs it,
    /// to reserve against before sweeping.</summary>
    private async Task<(AttachmentId Id, string ObjectKey, ConversationId ConversationId)> SeedWithConversationIdAsync(
        AttachmentState state, DateTimeOffset createdAt)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        var objectKey = $"site/{siteId.Value}/conv/{conversationId.Value}/{Guid.NewGuid():N}.png";

        var attachment = Attachment.CreatePending(
            new AttachmentId(Guid.NewGuid()), siteId, conversationId, objectKey, "image/png", 5, createdAt);
        if (state == AttachmentState.Ready)
        {
            attachment.ConfirmReady(5, "image/png", createdAt);
        }

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(visitorId, siteId, createdAt));
        db.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, createdAt));
        db.Attachments.Add(attachment);
        await db.SaveChangesAsync();

        return (attachment.Id, objectKey, conversationId);
    }

    private async Task UploadRealObjectAsync(string objectKey)
    {
        var presigned = await fixture.FileStorage.CreateUploadAsync(
            new ObjectKey(objectKey), new UploadConstraints("image/png", 5, TimeSpan.FromMinutes(5)), CancellationToken.None);
        using var http = new HttpClient();
        using var content = new ByteArrayContent("12345"u8.ToArray());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        var response = await http.PutAsync(presigned.Url, content);
        response.EnsureSuccessStatusCode();
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
