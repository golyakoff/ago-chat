using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.BulkDeleteSiteAttachments;
using Ago.Chat.Application.UseCases.CreateAttachment;
using Ago.Chat.Application.UseCases.GetAttachmentDownloadUrl;
using Ago.Chat.Application.UseCases.GetSiteAttachmentEgress;
using Ago.Chat.Application.UseCases.GetSiteAttachmentStorageSummary;
using Ago.Chat.Application.UseCases.ListSiteAttachments;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `23-80`/`23-82`: the handler-level proofs <see cref="CrossTenantRouteIsolationTests"/>'s own new
/// route-level test does not reach - "the quota-used number matches enforcement," "a deleted
/// attachment leaves an honest transcript," and "counting is not free" (never-downloaded/duplicates
/// driven by real indexes, not summed rows). Real Postgres throughout (<see cref="PostgresFixture"/>),
/// no MinIO - none of these tests need real object bytes, only real attachment/message/budget rows,
/// the same "one fixture per genuinely-needed resource combination" reasoning
/// <see cref="AttachmentFixture"/>'s own remarks state for why the upload-flow tests need MinIO and
/// these do not.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SiteAttachmentStorageHandlersTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>`23-80`'s own first Done-when box, proven rather than asserted: reserve bytes through
    /// the *real* enforcement port (`23-76`'s own <see cref="ISiteAttachmentStorageBudget"/>), then
    /// read the number back through <see cref="GetSiteAttachmentStorageSummaryHandler"/> - the two
    /// numbers agree because both read the identical column, not because they were computed to
    /// match.</summary>
    [Fact]
    public async Task StorageSummary_UsedBytes_AgreesWithTheBudgetEnforcementFigure()
    {
        var (siteId, operatorId) = await SeedSiteWithConfigurePermissionAsync();
        const long declaredBytes = 5_000_000;
        var options = new AttachmentStorageQuotaOptions();

        await using (var db = fixture.CreateDbContext())
        {
            var reservation = await new SiteAttachmentStorageBudgetStore(db)
                .TryReserveAsync(siteId, declaredBytes, options.FreeTierTotalBytes, CancellationToken.None);
            Assert.True(reservation.Reserved);
        }

        await using var readDb = fixture.CreateDbContext();
        var handler = new GetSiteAttachmentStorageSummaryHandler(
            new AttachmentBudgetReadStore(fixture.DataSource),
            new SiteRepository(readDb),
            new BillingSubscriptionRepository(readDb),
            new PermissionChecker(readDb),
            options,
            new FixedClock(Now));

        var result = await handler.HandleAsync(new GetSiteAttachmentStorageSummary(siteId, operatorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(declaredBytes, result.Value.UsedBytes);
        Assert.Equal(options.FreeTierTotalBytes, result.Value.TotalBytes);
    }

    /// <summary>
    /// `23-80`'s "show what will be freed" promise, and the defect this item's own report names: `5-08`'s
    /// single-attachment delete never releases <see cref="ISiteAttachmentStorageBudget"/>'s reservation,
    /// so "312 MB freed" would be a lie if this new bulk-delete path repeated that gap. It does not -
    /// proven by reading <c>sites.attachment_bytes_reserved</c> back after the call, not by trusting the
    /// handler's own returned total.
    /// </summary>
    [Fact]
    public async Task BulkDelete_ReleasesTheSiteBudget_AndMarksTheAttachmentDeleted()
    {
        var (siteId, operatorId) = await SeedSiteWithConfigurePermissionAsync();
        var (conversationId, _) = await SeedConversationAsync(siteId);
        var options = new AttachmentStorageQuotaOptions();
        const long sizeBytes = 2_000_000;

        var attachmentId = await CreateReadyAttachmentAsync(siteId, conversationId, sizeBytes);
        await using (var db = fixture.CreateDbContext())
        {
            await new SiteAttachmentStorageBudgetStore(db)
                .TryReserveAsync(siteId, sizeBytes, options.FreeTierTotalBytes, CancellationToken.None);
        }

        var fileStorage = new RecordingFileStorage();
        await using (var db = fixture.CreateDbContext())
        {
            var handler = new BulkDeleteSiteAttachmentsHandler(
                new AttachmentRepository(db),
                fileStorage,
                new SiteAttachmentStorageBudgetStore(db),
                new EfUnitOfWork(db),
                new PermissionChecker(db),
                NullLogger<BulkDeleteSiteAttachmentsHandler>.Instance);

            var result = await handler.HandleAsync(
                new BulkDeleteSiteAttachments(siteId, operatorId, [attachmentId]), CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.Equal(1, result.Value.DeletedCount);
            Assert.Equal(sizeBytes, result.Value.FreedBytes);
            Assert.Empty(result.Value.NotFoundIds);
            Assert.Equal(0, result.Value.AlreadyGoneCount);
        }

        Assert.Equal(0, await ReadReservedBytesAsync(siteId));
        Assert.Single(fileStorage.DeletedKeys);

        await using var verifyDb = fixture.CreateDbContext();
        var attachment = await verifyDb.Attachments.SingleAsync(a => a.Id == attachmentId);
        Assert.Equal(AttachmentState.Deleted, attachment.State);
    }

    /// <summary>
    /// `23-80`'s own "a deleted attachment must leave an honest transcript... not a broken link, not a
    /// missing image" - proven end to end. The message row (written by the real pipeline, not hand
    /// inserted) keeps its `attachment_id` untouched after the delete - `Attachment.MarkDeleted` never
    /// rewrites it, and this proves that stays true through the real write path, not only by reading
    /// the domain method's own body. What changes is what a client learns when it tries to *resolve*
    /// that reference: <c>GetAttachmentDownloadUrlHandler</c> now answers `Attachment.Removed` - a
    /// distinct, permanent code - rather than a generic not-ready error or an exception.
    /// </summary>
    [Fact]
    public async Task BulkDelete_LeavesTheMessagesAttachmentReferenceIntact_AndDownloadNowAnswersRemoved()
    {
        var (siteId, operatorId) = await SeedSiteWithConfigurePermissionAsync();
        var (conversationId, visitorId) = await SeedConversationAsync(siteId);
        const long sizeBytes = 1024;

        var attachmentId = await CreateReadyAttachmentAsync(siteId, conversationId, sizeBytes);

        var pipeline = new SynchronousMessagePipeline(fixture.DataSource);
        var sendResult = await pipeline.EnqueueAsync(
            new PendingMessage(conversationId, MessageAuthorKind.Visitor, visitorId.Value, new MessageBody("here is a file"), attachmentId),
            CancellationToken.None);
        Assert.True(sendResult.IsSuccess);

        await using (var db = fixture.CreateDbContext())
        {
            var handler = new BulkDeleteSiteAttachmentsHandler(
                new AttachmentRepository(db),
                new RecordingFileStorage(),
                new SiteAttachmentStorageBudgetStore(db),
                new EfUnitOfWork(db),
                new PermissionChecker(db),
                NullLogger<BulkDeleteSiteAttachmentsHandler>.Instance);

            var result = await handler.HandleAsync(
                new BulkDeleteSiteAttachments(siteId, operatorId, [attachmentId]), CancellationToken.None);
            Assert.True(result.IsSuccess);
        }

        // The message row itself, read directly - still references the same attachment id.
        var storedAttachmentId = await ReadMessageAttachmentIdAsync(conversationId);
        Assert.Equal(attachmentId.Value, storedAttachmentId);

        // Resolving that reference now answers a distinct, permanent "removed" - not NotFound, not
        // NotReady, not an exception.
        await using var downloadDb = fixture.CreateDbContext();
        var downloadHandler = new GetAttachmentDownloadUrlHandler(
            new AttachmentRepository(downloadDb),
            new ConversationRepository(downloadDb),
            new FakeFileStorage(),
            new PermissionChecker(downloadDb),
            new NoOpCache(),
            new NoOpEgressMeter(),
            new AttachmentOptions(),
            new FixedClock(Now),
            NullLogger<GetAttachmentDownloadUrlHandler>.Instance);

        var downloadResult = await downloadHandler.HandleAsVisitorAsync(
            new GetAttachmentDownloadUrlAsVisitor(attachmentId, visitorId), CancellationToken.None);

        Assert.True(downloadResult.IsFailure);
        Assert.Equal("Attachment.Removed", downloadResult.Error!.Value.Code);
    }

    /// <summary>`23-80`'s "never downloaded" filter - the one signal on the screen that "carries any
    /// judgement about value" (the item's own words). Proven against real Postgres: an attachment a
    /// download handler has recorded against is excluded, one it has not is included.</summary>
    [Fact]
    public async Task ListSiteAttachments_NeverDownloadedFilter_ExcludesAnAttachmentOnceItIsDownloaded()
    {
        var (siteId, operatorId) = await SeedSiteWithConfigurePermissionAsync();
        var (conversationId, visitorId) = await SeedConversationAsync(siteId);

        var downloadedId = await CreateReadyAttachmentAsync(siteId, conversationId, 100);
        var neverDownloadedId = await CreateReadyAttachmentAsync(siteId, conversationId, 200);

        await using (var db = fixture.CreateDbContext())
        {
            var downloadHandler = new GetAttachmentDownloadUrlHandler(
                new AttachmentRepository(db),
                new ConversationRepository(db),
                new FakeFileStorage(),
                new PermissionChecker(db),
                new NoOpCache(),
                new NoOpEgressMeter(),
                new AttachmentOptions(),
                new FixedClock(Now),
                NullLogger<GetAttachmentDownloadUrlHandler>.Instance);

            var result = await downloadHandler.HandleAsVisitorAsync(
                new GetAttachmentDownloadUrlAsVisitor(downloadedId, visitorId), CancellationToken.None);
            Assert.True(result.IsSuccess);
        }

        await using var readDb = fixture.CreateDbContext();
        var listHandler = new ListSiteAttachmentsHandler(new SiteAttachmentListReadStore(fixture.DataSource), new PermissionChecker(readDb));

        var page = await listHandler.HandleAsync(
            new ListSiteAttachments(siteId, operatorId, AttachmentListSort.SizeDescending, AttachmentListFilterKind.NeverDownloaded, null, null),
            CancellationToken.None);

        Assert.True(page.IsSuccess);
        var ids = page.Value.Items.Select(i => i.Id).ToList();
        Assert.Contains(neverDownloadedId, ids);
        Assert.DoesNotContain(downloadedId, ids);
    }

    /// <summary>`23-80`'s duplicate view - two attachments that share `ContentHash` (23-76's own
    /// dedup column) are both flagged, and one that shares nothing is not.</summary>
    [Fact]
    public async Task ListSiteAttachments_DuplicatesFilter_ReturnsOnlyAttachmentsSharingAContentHash()
    {
        var (siteId, operatorId) = await SeedSiteWithConfigurePermissionAsync();
        var (conversationId, _) = await SeedConversationAsync(siteId);

        var firstId = await CreateReadyAttachmentAsync(siteId, conversationId, 100, contentHash: "hash-a");
        var secondId = await CreateReadyAttachmentAsync(siteId, conversationId, 100, contentHash: "hash-a");
        var uniqueId = await CreateReadyAttachmentAsync(siteId, conversationId, 100, contentHash: "hash-b");

        await using var readDb = fixture.CreateDbContext();
        var listHandler = new ListSiteAttachmentsHandler(new SiteAttachmentListReadStore(fixture.DataSource), new PermissionChecker(readDb));

        var page = await listHandler.HandleAsync(
            new ListSiteAttachments(siteId, operatorId, AttachmentListSort.SizeDescending, AttachmentListFilterKind.Duplicates, null, null),
            CancellationToken.None);

        Assert.True(page.IsSuccess);
        var ids = page.Value.Items.Select(i => i.Id).ToList();
        Assert.Contains(firstId, ids);
        Assert.Contains(secondId, ids);
        Assert.DoesNotContain(uniqueId, ids);
        Assert.All(page.Value.Items, i => Assert.True(i.IsDuplicate));
    }

    /// <summary>`23-80`'s "largest conversations, not only largest files" view - a conversation with
    /// several small attachments outweighs one with a single larger one.</summary>
    [Fact]
    public async Task LargestConversations_OrdersByTotalBytes_NotByAnySingleAttachment()
    {
        var (siteId, operatorId) = await SeedSiteWithConfigurePermissionAsync();
        var (manySmallConversationId, _) = await SeedConversationAsync(siteId);
        var (oneBigConversationId, _) = await SeedConversationAsync(siteId);

        await CreateReadyAttachmentAsync(siteId, manySmallConversationId, 40);
        await CreateReadyAttachmentAsync(siteId, manySmallConversationId, 40);
        await CreateReadyAttachmentAsync(siteId, manySmallConversationId, 40);
        await CreateReadyAttachmentAsync(siteId, oneBigConversationId, 100);

        await using var readDb = fixture.CreateDbContext();
        var handler = new GetLargestConversationsForSiteHandler(new SiteAttachmentListReadStore(fixture.DataSource), new PermissionChecker(readDb));

        var result = await handler.HandleAsync(new GetLargestConversationsForSite(siteId, operatorId, null), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var top = result.Value[0];
        Assert.Equal(manySmallConversationId, top.ConversationId);
        Assert.Equal(120, top.TotalBytes);
        Assert.Equal(3, top.AttachmentCount);
    }

    /// <summary>`23-82`'s own second Done-when box - the measured figure is readable back, and it is
    /// exactly what the real download handler recorded, not a second, independently-computed
    /// number.</summary>
    [Fact]
    public async Task Egress_ReflectsWhatTheRealDownloadHandlerRecorded()
    {
        var (siteId, operatorId) = await SeedSiteWithConfigurePermissionAsync();
        var (conversationId, visitorId) = await SeedConversationAsync(siteId);
        const long sizeBytes = 777;
        var attachmentId = await CreateReadyAttachmentAsync(siteId, conversationId, sizeBytes);

        await using (var db = fixture.CreateDbContext())
        {
            var downloadHandler = new GetAttachmentDownloadUrlHandler(
                new AttachmentRepository(db),
                new ConversationRepository(db),
                new FakeFileStorage(),
                new PermissionChecker(db),
                new NoOpCache(),
                new AttachmentEgressMeterStore(fixture.DataSource),
                new AttachmentOptions(),
                new FixedClock(Now),
                NullLogger<GetAttachmentDownloadUrlHandler>.Instance);

            var result = await downloadHandler.HandleAsVisitorAsync(
                new GetAttachmentDownloadUrlAsVisitor(attachmentId, visitorId), CancellationToken.None);
            Assert.True(result.IsSuccess);
        }

        await using var readDb = fixture.CreateDbContext();
        var egressHandler = new GetSiteAttachmentEgressHandler(
            new AttachmentEgressReadStore(fixture.DataSource), new PermissionChecker(readDb), new FixedClock(Now));

        var egress = await egressHandler.HandleAsync(new GetSiteAttachmentEgress(siteId, operatorId, null), CancellationToken.None);

        Assert.True(egress.IsSuccess);
        Assert.Equal(1, egress.Value.DownloadCount);
        Assert.Equal(sizeBytes, egress.Value.BytesOut);
    }

    private async Task<(SiteId SiteId, OperatorId OperatorId)> SeedSiteWithConfigurePermissionAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Operators.Add(new Operator(
            operatorId, siteId, OperatorStatus.Online, capacity: 5, externalSubjectId: $"ext_{operatorId.Value:N}"));
        var roleId = Guid.NewGuid();
        db.Roles.Add(new RoleRecord
        {
            Id = roleId,
            SiteId = siteId,
            Name = "Admin",
            Permissions = [Permission.SiteConfigure.Value],
        });
        db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
        await db.SaveChangesAsync();

        return (siteId, operatorId);
    }

    private async Task<(ConversationId ConversationId, VisitorId VisitorId)> SeedConversationAsync(SiteId siteId)
    {
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        db.Visitors.Add(new Visitor(visitorId, siteId, Now));
        db.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, Now));
        await db.SaveChangesAsync();

        return (conversationId, visitorId);
    }

    private async Task<AttachmentId> CreateReadyAttachmentAsync(
        SiteId siteId, ConversationId conversationId, long sizeBytes, string? contentHash = null)
    {
        var attachmentId = new AttachmentId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        var attachment = Attachment.CreatePending(
            attachmentId, siteId, conversationId, $"site/{siteId.Value:N}/{attachmentId.Value:N}.png", "image/png", sizeBytes, Now);
        attachment.ConfirmReady(sizeBytes, "image/png", Now);
        if (contentHash is not null)
        {
            attachment.SetContentHash(contentHash);
        }

        db.Attachments.Add(attachment);
        await db.SaveChangesAsync();

        return attachmentId;
    }

    private async Task<long> ReadReservedBytesAsync(SiteId siteId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT attachment_bytes_reserved FROM sites WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", siteId.Value);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<Guid?> ReadMessageAttachmentIdAsync(ConversationId conversationId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT attachment_id FROM messages WHERE conversation_id = @id", connection);
        command.Parameters.AddWithValue("id", conversationId.Value);
        var result = await command.ExecuteScalarAsync();
        return result is Guid guid ? guid : null;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    /// <summary>Records every key <see cref="DeleteAsync"/> is called with - `FakeFileStorage`'s own
    /// silent no-op is not enough for a test that needs to prove the storage-object delete actually
    /// happened.</summary>
    private sealed class RecordingFileStorage : IFileStorage
    {
        public List<string> DeletedKeys { get; } = [];

        public Task<PresignedUpload> CreateUploadAsync(ObjectKey key, UploadConstraints constraints, CancellationToken cancellationToken) =>
            Task.FromResult(new PresignedUpload(new Uri($"https://fake-storage.test/{key.Value}"), DateTimeOffset.UtcNow.Add(constraints.Lifetime)));

        public Task<Uri> CreateDownloadUrlAsync(ObjectKey key, TimeSpan lifetime, CancellationToken cancellationToken) =>
            Task.FromResult(new Uri($"https://fake-storage.test/{key.Value}?download"));

        public Task<ObjectMetadata?> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
            Task.FromResult<ObjectMetadata?>(null);

        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken)
        {
            DeletedKeys.Add(key.Value);
            return Task.CompletedTask;
        }
    }

    /// <summary>A no-op <see cref="IAttachmentEgressMeter"/> for tests about something other than the
    /// egress aggregate itself - the identical "stand in for the port a handler needs but this test
    /// does not care about" reasoning <see cref="FakeFileStorage"/>'s own remarks give.</summary>
    private sealed class NoOpEgressMeter : IAttachmentEgressMeter
    {
        public Task RecordAsync(SiteId siteId, DateOnly periodMonth, long bytes, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
