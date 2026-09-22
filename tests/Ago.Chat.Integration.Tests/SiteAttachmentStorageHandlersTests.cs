using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.BulkDeleteSiteAttachments;
using Ago.Chat.Application.UseCases.CreateAttachment;
using Ago.Chat.Application.UseCases.GetAttachmentDownloadUrl;
using Ago.Chat.Application.UseCases.GetSiteAttachmentEgress;
using Ago.Chat.Application.UseCases.CreateCheckoutSession;
using Ago.Chat.Application.UseCases.PurchaseDownloadOverage;
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
            new SiteRepository(downloadDb),
            new FakeFileStorage(),
            new PermissionChecker(downloadDb),
            new NoOpCache(),
            new NoOpEgressMeter(),
            new AttachmentEgressReadStore(fixture.DataSource),
            new DownloadThresholdReadStore(fixture.DataSource),
            new DownloadOverageReadStore(fixture.DataSource),
            new PriceCatalogRepository(downloadDb),
            new AttachmentOptions(),
            new FixedClock(Now),
            new UuidV7Generator(),
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
                new SiteRepository(db),
                new FakeFileStorage(),
                new PermissionChecker(db),
                new NoOpCache(),
                new NoOpEgressMeter(),
                new AttachmentEgressReadStore(fixture.DataSource),
                new DownloadThresholdReadStore(fixture.DataSource),
                new DownloadOverageReadStore(fixture.DataSource),
                new PriceCatalogRepository(db),
                new AttachmentOptions(),
                new FixedClock(Now),
                new UuidV7Generator(),
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
                new SiteRepository(db),
                new FakeFileStorage(),
                new PermissionChecker(db),
                new NoOpCache(),
                new AttachmentEgressMeterStore(fixture.DataSource),
                new AttachmentEgressReadStore(fixture.DataSource),
                new DownloadThresholdReadStore(fixture.DataSource),
                new DownloadOverageReadStore(fixture.DataSource),
                new PriceCatalogRepository(db),
                new AttachmentOptions(),
                new FixedClock(Now),
                new UuidV7Generator(),
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

    // ------------------------------------------------------------------------------------------
    // `25-83`: the hard download-block threshold, against real Postgres rather than
    // `Ago.Chat.Application.Tests`' own mocked `FakeDownloadThresholdReadStore`/
    // `FakeAttachmentEgressReadStore` - the real `tier_download_thresholds` table
    // (`DownloadThresholdReadStore`) and the real `site_attachment_egress` row the real
    // `AttachmentEgressMeterStore` writes, read back by the real `GetAttachmentDownloadUrlHandler`.
    // Each test seeds its own unique tier name (`SeedSiteWithConversationReadPermissionAndThresholdAsync`)
    // rather than reusing the migration-seeded "free"/"starter" rows - `tier_download_thresholds` is
    // shared reference data across this whole collection's tests, and a distinct tier per test is what
    // keeps one test's own threshold from leaking into another's.
    // ------------------------------------------------------------------------------------------

    /// <summary>`docs/backlog/25-83-*.md`'s own Done-when: "every presigned GET refuses... proven by
    /// fault injection" - the visitor half.</summary>
    [Fact]
    public async Task HandleAsVisitorAsync_WhenTheSiteIsAtItsHardThreshold_RefusesTheDownload_OverRealPostgres()
    {
        var (siteId, _) = await SeedSiteWithConversationReadPermissionAndThresholdAsync(softThresholdBytes: 100, hardThresholdBytes: 200);
        var (conversationId, visitorId) = await SeedConversationAsync(siteId);
        var attachmentId = await CreateReadyAttachmentAsync(siteId, conversationId, 50);

        // The real write path, not a hand-inserted row - the same AttachmentEgressMeterStore
        // GetAttachmentDownloadUrlHandler.RecordDownloadAsync itself calls.
        await new AttachmentEgressMeterStore(fixture.DataSource).RecordAsync(
            siteId, new DateOnly(Now.Year, Now.Month, 1), bytes: 200, CancellationToken.None);

        await using var db = fixture.CreateDbContext();
        var result = await CreateDownloadHandler(db).HandleAsVisitorAsync(
            new GetAttachmentDownloadUrlAsVisitor(attachmentId, visitorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Attachment.DownloadBlocked", result.Error!.Value.Code);
    }

    /// <summary>The operator half of the identical fault-injection proof - `docs/backlog/25-83-*.md`'s
    /// own explicit "no carve-out for either caller" decision, checked against real Postgres rather
    /// than only `GetAttachmentDownloadUrlHandlerTests`' own mocked equivalent.</summary>
    [Fact]
    public async Task HandleAsOperatorAsync_WhenTheSiteIsAtItsHardThreshold_RefusesTheDownload_OverRealPostgres()
    {
        var (siteId, operatorId) = await SeedSiteWithConversationReadPermissionAndThresholdAsync(softThresholdBytes: 100, hardThresholdBytes: 200);
        var (conversationId, _) = await SeedConversationAsync(siteId);
        var attachmentId = await CreateReadyAttachmentAsync(siteId, conversationId, 50);
        await AssignOperatorToConversationAsync(conversationId, operatorId);

        await new AttachmentEgressMeterStore(fixture.DataSource).RecordAsync(
            siteId, new DateOnly(Now.Year, Now.Month, 1), bytes: 200, CancellationToken.None);

        await using var db = fixture.CreateDbContext();
        var result = await CreateDownloadHandler(db).HandleAsOperatorAsync(
            new GetAttachmentDownloadUrlAsOperator(attachmentId, operatorId, siteId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Attachment.DownloadBlocked", result.Error!.Value.Code);
    }

    /// <summary>Below the hard threshold, the identical real-Postgres setup still succeeds - the
    /// negative control that proves the two tests above are refused by the threshold itself, not by
    /// some other real-Postgres wiring difference from `Egress_ReflectsWhatTheRealDownloadHandlerRecorded`
    /// right above.</summary>
    [Fact]
    public async Task HandleAsVisitorAsync_WhenBelowTheHardThreshold_StillSucceeds_OverRealPostgres()
    {
        var (siteId, _) = await SeedSiteWithConversationReadPermissionAndThresholdAsync(softThresholdBytes: 100, hardThresholdBytes: 200);
        var (conversationId, visitorId) = await SeedConversationAsync(siteId);
        var attachmentId = await CreateReadyAttachmentAsync(siteId, conversationId, 50);

        await new AttachmentEgressMeterStore(fixture.DataSource).RecordAsync(
            siteId, new DateOnly(Now.Year, Now.Month, 1), bytes: 199, CancellationToken.None);

        await using var db = fixture.CreateDbContext();
        var result = await CreateDownloadHandler(db).HandleAsVisitorAsync(
            new GetAttachmentDownloadUrlAsVisitor(attachmentId, visitorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    /// <summary>`docs/backlog/25-83-*.md`'s own Done-when: "a visitor's refused download drops a
    /// locale-aware system message into that exact conversation" - proven this time against a real
    /// `Conversation` row a second, independent read genuinely finds afterwards, not only against
    /// `FakeConversationRepository`'s in-memory list (`GetAttachmentDownloadUrlHandlerTests`'s own
    /// equivalent). English default; <see cref="HandleAsVisitorAsync_WhenBlocked_PersistsTheRussianSystemMessage_InRealPostgres"/>
    /// is the Russian half.</summary>
    [Fact]
    public async Task HandleAsVisitorAsync_WhenBlocked_PersistsTheSystemMessage_InRealPostgres()
    {
        var (siteId, _) = await SeedSiteWithConversationReadPermissionAndThresholdAsync(softThresholdBytes: 100, hardThresholdBytes: 200);
        var (conversationId, visitorId) = await SeedConversationAsync(siteId);
        var attachmentId = await CreateReadyAttachmentAsync(siteId, conversationId, 50);
        await new AttachmentEgressMeterStore(fixture.DataSource).RecordAsync(
            siteId, new DateOnly(Now.Year, Now.Month, 1), bytes: 200, CancellationToken.None);

        await using (var db = fixture.CreateDbContext())
        {
            await CreateDownloadHandler(db).HandleAsVisitorAsync(
                new GetAttachmentDownloadUrlAsVisitor(attachmentId, visitorId), CancellationToken.None);
        }

        await using var verifyDb = fixture.CreateDbContext();
        var conversation = await new ConversationRepository(verifyDb).GetByIdAsync(conversationId, CancellationToken.None);
        var systemMessage = Assert.Single(conversation!.Messages, m => m.AuthorKind == MessageAuthorKind.System);
        Assert.Contains("monthly download limit", systemMessage.Body.Value);
    }

    /// <summary>The Russian half - `RouteConversationToModuleHandler`'s own four texts are the
    /// precedent this system message follows (`GetAttachmentDownloadUrlHandler.DownloadBlockedText`'s
    /// own remarks): `Locale.Ru` on the site gets natural Russian wording, checked here against a row
    /// a fresh read genuinely finds, not the handler's in-process `Conversation` instance.</summary>
    [Fact]
    public async Task HandleAsVisitorAsync_WhenBlocked_PersistsTheRussianSystemMessage_InRealPostgres()
    {
        var (siteId, _) = await SeedSiteWithConversationReadPermissionAndThresholdAsync(softThresholdBytes: 100, hardThresholdBytes: 200);
        await using (var localeDb = fixture.CreateDbContext())
        {
            var siteRepository = new SiteRepository(localeDb);
            var site = await siteRepository.GetByIdAsync(siteId, CancellationToken.None);
            site!.UpdateLocale(Locale.Ru, Now);
            await siteRepository.SaveAsync(site, CancellationToken.None);
        }

        var (conversationId, visitorId) = await SeedConversationAsync(siteId);
        var attachmentId = await CreateReadyAttachmentAsync(siteId, conversationId, 50);
        await new AttachmentEgressMeterStore(fixture.DataSource).RecordAsync(
            siteId, new DateOnly(Now.Year, Now.Month, 1), bytes: 200, CancellationToken.None);

        await using (var db = fixture.CreateDbContext())
        {
            await CreateDownloadHandler(db).HandleAsVisitorAsync(
                new GetAttachmentDownloadUrlAsVisitor(attachmentId, visitorId), CancellationToken.None);
        }

        await using var verifyDb = fixture.CreateDbContext();
        var conversation = await new ConversationRepository(verifyDb).GetByIdAsync(conversationId, CancellationToken.None);
        var systemMessage = Assert.Single(conversation!.Messages, m => m.AuthorKind == MessageAuthorKind.System);
        Assert.Contains("месячного лимита скачиваний", systemMessage.Body.Value);
    }

    /// <summary>`docs/backlog/25-83-*.md`'s own explicit override Done-when, proven in both directions
    /// against real Postgres: blocked before the platform owner's own <see cref="Site.GrantDownloadBlockExemption"/>
    /// is applied, genuinely bypassed once it is. <see cref="OwnerDownloadBlockExemptionEndpointTests"/>
    /// proves the identical fact through the real owner-only HTTP route rather than the domain method
    /// called directly, as this test does.</summary>
    [Fact]
    public async Task HandleAsVisitorAsync_WhenTheSiteIsExempt_BypassesTheHardBlock_OverRealPostgres()
    {
        var (siteId, _) = await SeedSiteWithConversationReadPermissionAndThresholdAsync(softThresholdBytes: 100, hardThresholdBytes: 200);
        var (conversationId, visitorId) = await SeedConversationAsync(siteId);
        var attachmentId = await CreateReadyAttachmentAsync(siteId, conversationId, 50);
        await new AttachmentEgressMeterStore(fixture.DataSource).RecordAsync(
            siteId, new DateOnly(Now.Year, Now.Month, 1), bytes: 500, CancellationToken.None);

        await using (var blockedDb = fixture.CreateDbContext())
        {
            var blockedResult = await CreateDownloadHandler(blockedDb).HandleAsVisitorAsync(
                new GetAttachmentDownloadUrlAsVisitor(attachmentId, visitorId), CancellationToken.None);
            Assert.True(blockedResult.IsFailure);
            Assert.Equal("Attachment.DownloadBlocked", blockedResult.Error!.Value.Code);
        }

        await using (var exemptDb = fixture.CreateDbContext())
        {
            var siteRepository = new SiteRepository(exemptDb);
            var site = await siteRepository.GetByIdAsync(siteId, CancellationToken.None);
            site!.GrantDownloadBlockExemption("owner@example.com", "goodwill exception", Now);
            await siteRepository.SaveAsync(site, CancellationToken.None);
        }

        await using var allowedDb = fixture.CreateDbContext();
        var allowedResult = await CreateDownloadHandler(allowedDb).HandleAsVisitorAsync(
            new GetAttachmentDownloadUrlAsVisitor(attachmentId, visitorId), CancellationToken.None);
        Assert.True(allowedResult.IsSuccess);
    }

    private GetAttachmentDownloadUrlHandler CreateDownloadHandler(AgoChatDbContext db) =>
        CreateDownloadHandler(db, Now);

    /// <summary>`25-84`: the same handler with the clock moved - what proves a paid month does not carry
    /// into the next one without inventing an expiry job to drive.</summary>
    private GetAttachmentDownloadUrlHandler CreateDownloadHandler(AgoChatDbContext db, DateTimeOffset now) => new(
        new AttachmentRepository(db),
        new ConversationRepository(db),
        new SiteRepository(db),
        new FakeFileStorage(),
        new PermissionChecker(db),
        new NoOpCache(),
        new NoOpEgressMeter(),
        new AttachmentEgressReadStore(fixture.DataSource),
        new DownloadThresholdReadStore(fixture.DataSource),
        new DownloadOverageReadStore(fixture.DataSource),
        new PriceCatalogRepository(db),
        new AttachmentOptions(),
        new FixedClock(now),
        new UuidV7Generator(),
        NullLogger<GetAttachmentDownloadUrlHandler>.Instance);

    /// <summary>A fresh, unique tariff tier per call - `tier_download_thresholds` is shared reference
    /// data across every test in this collection (the migration's own two seeded rows,
    /// `Stage25AddTierDownloadThresholds`'s own remarks), so a test-owned tier name is what keeps one
    /// test's own threshold from being visible to, or overwritten by, another's. Grants
    /// `conversation:read`, not `site:configure`
    /// (<see cref="SeedSiteWithConfigurePermissionAsync"/>'s own grant) - the permission
    /// `GetAttachmentDownloadUrlHandler.HandleAsOperatorAsync` actually checks.</summary>
    private async Task<(SiteId SiteId, OperatorId OperatorId)> SeedSiteWithConversationReadPermissionAndThresholdAsync(
        long softThresholdBytes, long hardThresholdBytes, decimal? autoBillCapRub = null)
    {
        var tier = $"test_{Guid.NewGuid():N}";
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        {
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO tier_download_thresholds (tier, soft_threshold_bytes, hard_threshold_bytes, auto_bill_cap_rub, updated_at, updated_by)
                VALUES (@tier, @soft, @hard, @cap, now(), 'SiteAttachmentStorageHandlersTests')
                """,
                connection);
            command.Parameters.AddWithValue("tier", tier);
            command.Parameters.AddWithValue("soft", softThresholdBytes);
            command.Parameters.AddWithValue("hard", hardThresholdBytes);
            // `25-84`: NULL means uncapped, which is what every `25-83` test here means by not passing one.
            command.Parameters.AddWithValue("cap", (object?)autoBillCapRub ?? DBNull.Value);
            await command.ExecuteNonQueryAsync();
        }

        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", [], tier: tier));
        db.Operators.Add(new Operator(
            operatorId, siteId, OperatorStatus.Online, capacity: 5, externalSubjectId: $"ext_{operatorId.Value:N}"));
        var roleId = Guid.NewGuid();
        db.Roles.Add(new RoleRecord
        {
            Id = roleId,
            SiteId = siteId,
            Name = "Admin",
            Permissions = [Permission.ConversationRead.Value],
        });
        db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
        await db.SaveChangesAsync();

        return (siteId, operatorId);
    }

    private async Task AssignOperatorToConversationAsync(ConversationId conversationId, OperatorId operatorId)
    {
        await using var db = fixture.CreateDbContext();
        var repository = new ConversationRepository(db);
        var conversation = await repository.GetByIdAsync(conversationId, CancellationToken.None);
        // `25-221`: a brand-new conversation starts Pending, not Waiting - graduate it with the
        // visitor's own real first message before AssignTo, which still only accepts Waiting.
        conversation!.AddVisitorMessage(conversation.VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        conversation.AssignTo(operatorId, Now);
        await repository.SaveAsync(conversation, CancellationToken.None);
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

    // ----------------------------------------------------------------------------------------------
    // `25-84`: the paid way past `25-83`'s own block, against real Postgres - the real
    // `download_overage_charges` table, the real `published_price_versions` row the migration seeded,
    // and the real `BillingWebhookApplier` that promotes a pending checkout. Each test starts from the
    // identical blocked state `HandleAsVisitorAsync_WhenTheSiteIsAtItsHardThreshold_RefusesTheDownload_OverRealPostgres`
    // above proves is refused, so a pass here is evidence about the escape hatch and nothing else.
    // ----------------------------------------------------------------------------------------------

    private const long OneGibibyte = 1024L * 1024L * 1024L;

    /// <summary>`docs/backlog/25-84-*.md`'s own Done-when: "auto-bill accrues... the tenant never
    /// blocks", with zero action from the tenant - no checkout, no row in
    /// <c>download_overage_charges</c> at all, just the owner's toggle.</summary>
    [Fact]
    public async Task HandleAsVisitorAsync_WhenOnAutoBill_IsNotBlocked_OverRealPostgres()
    {
        var (siteId, _) = await SeedSiteWithConversationReadPermissionAndThresholdAsync(
            softThresholdBytes: OneGibibyte / 2, hardThresholdBytes: OneGibibyte);
        var (conversationId, visitorId) = await SeedConversationAsync(siteId);
        var attachmentId = await CreateReadyAttachmentAsync(siteId, conversationId, 50);
        await SetBillingModeAsync(siteId, DownloadOverageBillingMode.AutoBill);

        await new AttachmentEgressMeterStore(fixture.DataSource).RecordAsync(
            siteId, new DateOnly(Now.Year, Now.Month, 1), bytes: 3 * OneGibibyte, CancellationToken.None);

        await using var db = fixture.CreateDbContext();
        var result = await CreateDownloadHandler(db).HandleAsVisitorAsync(
            new GetAttachmentDownloadUrlAsVisitor(attachmentId, visitorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    /// <summary><b>The immediate-unblock proof, end to end.</b> `docs/backlog/25-84-*.md`'s own literal
    /// wording - "the tenant's next presigned GET after payment succeeds without waiting for any billing
    /// cycle boundary." Blocked, then a real checkout through the real handler, then still blocked
    /// (the pending row is not payment), then the real ЮKassa webhook applier settles it, then the very
    /// next download succeeds. No clock is advanced, no renewal runs, no job ticks between the webhook
    /// and the successful download.</summary>
    [Fact]
    public async Task ManualPath_PayThenDownload_UnblocksImmediately_OverRealPostgres()
    {
        var (siteId, operatorId) = await SeedSiteWithConversationReadPermissionAndThresholdAsync(
            softThresholdBytes: OneGibibyte / 2, hardThresholdBytes: OneGibibyte);
        var (conversationId, visitorId) = await SeedConversationAsync(siteId);
        var attachmentId = await CreateReadyAttachmentAsync(siteId, conversationId, 50);
        await GrantSiteConfigureAsync(siteId, operatorId);

        await new AttachmentEgressMeterStore(fixture.DataSource).RecordAsync(
            siteId, new DateOnly(Now.Year, Now.Month, 1), bytes: 3 * OneGibibyte, CancellationToken.None);

        // 1. Manual is the default, and the default is blocked.
        await using (var db = fixture.CreateDbContext())
        {
            var blocked = await CreateDownloadHandler(db).HandleAsVisitorAsync(
                new GetAttachmentDownloadUrlAsVisitor(attachmentId, visitorId), CancellationToken.None);
            Assert.True(blocked.IsFailure);
            Assert.Equal("Attachment.DownloadBlocked", blocked.Error!.Value.Code);
        }

        // 2. A real checkout through the real handler, against the real migration-seeded 100 RUB/GB
        //    price - 2 GiB over, so exactly 200 RUB.
        var yooKassa = new RecordingYooKassaClient("pmt_overage_" + Guid.NewGuid().ToString("N"));
        await using (var db = fixture.CreateDbContext())
        {
            var purchase = new PurchaseDownloadOverageHandler(
                new SiteRepository(db), new PermissionChecker(db),
                new AttachmentEgressReadStore(fixture.DataSource),
                new DownloadThresholdReadStore(fixture.DataSource),
                new DownloadOverageReadStore(fixture.DataSource),
                new DownloadOverageChargeRepository(db),
                new PriceCatalogRepository(db),
                yooKassa,
                new BillingOptions { CheckoutReturnUrl = "https://console.example/billing" },
                new UuidV7Generator(), new FixedClock(Now));

            var checkout = await purchase.HandleAsync(
                new PurchaseDownloadOverage(operatorId, siteId), CancellationToken.None);

            Assert.True(checkout.IsSuccess);
            Assert.Equal(200.00m, checkout.Value.AmountRub);
            Assert.Equal(2 * OneGibibyte, checkout.Value.BytesOver);
        }

        // 3. Still blocked - a pending row is not payment ("never the redirect alone").
        await using (var db = fixture.CreateDbContext())
        {
            var stillBlocked = await CreateDownloadHandler(db).HandleAsVisitorAsync(
                new GetAttachmentDownloadUrlAsVisitor(attachmentId, visitorId), CancellationToken.None);
            Assert.True(stillBlocked.IsFailure);
        }

        // 4. The real webhook applier, on a real `payment.succeeded`.
        await using (var db = fixture.CreateDbContext())
        {
            var applier = new BillingWebhookApplier(db, new NoOpOutboxWriter(), new UuidV7Generator());
            var applied = await applier.ApplyAsync(
                new BillingWebhookApplyRequest(yooKassa.PaymentId, "payment.succeeded", null, Now), CancellationToken.None);

            var settled = Assert.IsType<BillingWebhookApplyResult.DownloadOverageSettled>(applied);
            Assert.Equal(siteId, settled.SiteId);
            Assert.Equal(200.00m, settled.AmountRub);
        }

        // 5. The very next presigned GET succeeds. Nothing else happened in between.
        await using (var db = fixture.CreateDbContext())
        {
            var afterPaying = await CreateDownloadHandler(db).HandleAsVisitorAsync(
                new GetAttachmentDownloadUrlAsVisitor(attachmentId, visitorId), CancellationToken.None);
            Assert.True(afterPaying.IsSuccess);
        }
    }

    /// <summary>The paid unblock does not carry into the next calendar month -
    /// `docs/backlog/25-84-*.md`'s own "does not carry into the next month... no refund or rollover
    /// credit." The identical settled charge, read by a handler whose clock says the month rolled over,
    /// leaves the tenant blocked again. Proven without any expiry job existing at all, because the
    /// <c>period_month</c> stamp on the row is what expires.</summary>
    [Fact]
    public async Task ASettledCheckout_DoesNotUnblockTheFollowingMonth_OverRealPostgres()
    {
        var (siteId, _) = await SeedSiteWithConversationReadPermissionAndThresholdAsync(
            softThresholdBytes: OneGibibyte / 2, hardThresholdBytes: OneGibibyte);
        var (conversationId, visitorId) = await SeedConversationAsync(siteId);
        var attachmentId = await CreateReadyAttachmentAsync(siteId, conversationId, 50);

        var nextMonth = Now.AddMonths(1);
        var meter = new AttachmentEgressMeterStore(fixture.DataSource);
        await meter.RecordAsync(siteId, new DateOnly(Now.Year, Now.Month, 1), 3 * OneGibibyte, CancellationToken.None);
        await meter.RecordAsync(siteId, new DateOnly(nextMonth.Year, nextMonth.Month, 1), 3 * OneGibibyte, CancellationToken.None);

        // A real, settled checkout - but stamped with this month.
        await using (var db = fixture.CreateDbContext())
        {
            var charge = DownloadOverageCharge.PendingCheckout(
                new DownloadOverageChargeId(Guid.NewGuid()), siteId, new DateOnly(Now.Year, Now.Month, 1),
                bytesOver: 2 * OneGibibyte, amountRub: 200m, priceVersion: 1,
                yooKassaPaymentId: "pmt_" + Guid.NewGuid().ToString("N"), createdAt: Now);
            charge.MarkSucceeded(Now);
            await new DownloadOverageChargeRepository(db).SaveAsync(charge, CancellationToken.None);
        }

        // This month: unblocked.
        await using (var db = fixture.CreateDbContext())
        {
            Assert.True((await CreateDownloadHandler(db).HandleAsVisitorAsync(
                new GetAttachmentDownloadUrlAsVisitor(attachmentId, visitorId), CancellationToken.None)).IsSuccess);
        }

        // Next month: blocked again, with the identical row still in the table.
        await using (var db = fixture.CreateDbContext())
        {
            var result = await CreateDownloadHandler(db, nextMonth).HandleAsVisitorAsync(
                new GetAttachmentDownloadUrlAsVisitor(attachmentId, visitorId), CancellationToken.None);

            Assert.True(result.IsFailure);
            Assert.Equal("Attachment.DownloadBlocked", result.Error!.Value.Code);
        }
    }

    /// <summary>`docs/backlog/25-84-*.md`'s own second open question, answered and proven against real
    /// Postgres: the auto-bill cap is a real column on `tier_download_thresholds`, and reaching it
    /// returns an auto-billed tenant to the `25-83` block. 6 GiB over at the migration-seeded 100 RUB/GB
    /// is 600 RUB, past this tier's own 500 RUB cap.</summary>
    [Fact]
    public async Task HandleAsVisitorAsync_WhenOnAutoBill_AndPastTheTiersAutoBillCap_IsBlockedAgain_OverRealPostgres()
    {
        var (siteId, _) = await SeedSiteWithConversationReadPermissionAndThresholdAsync(
            softThresholdBytes: OneGibibyte / 2, hardThresholdBytes: OneGibibyte, autoBillCapRub: 500m);
        var (conversationId, visitorId) = await SeedConversationAsync(siteId);
        var attachmentId = await CreateReadyAttachmentAsync(siteId, conversationId, 50);
        await SetBillingModeAsync(siteId, DownloadOverageBillingMode.AutoBill);

        await new AttachmentEgressMeterStore(fixture.DataSource).RecordAsync(
            siteId, new DateOnly(Now.Year, Now.Month, 1), bytes: 7 * OneGibibyte, CancellationToken.None);

        await using var db = fixture.CreateDbContext();
        var result = await CreateDownloadHandler(db).HandleAsVisitorAsync(
            new GetAttachmentDownloadUrlAsVisitor(attachmentId, visitorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Attachment.DownloadBlocked", result.Error!.Value.Code);
    }

    /// <summary>The real Dapper outstanding query - the one a renewal sweeps from. Two months of real
    /// `site_attachment_egress` rows and one real settled charge, and the store computes the same
    /// arithmetic the fake does, against the real `SUM`/`bool_or` group.</summary>
    [Fact]
    public async Task DownloadOverageReadStore_ComputesOutstandingPerMonth_NetOfSettledCharges()
    {
        var (siteId, _) = await SeedSiteWithConversationReadPermissionAndThresholdAsync(
            softThresholdBytes: OneGibibyte / 2, hardThresholdBytes: OneGibibyte);
        var thisMonth = new DateOnly(Now.Year, Now.Month, 1);
        var nextMonth = new DateOnly(Now.AddMonths(1).Year, Now.AddMonths(1).Month, 1);

        var meter = new AttachmentEgressMeterStore(fixture.DataSource);
        await meter.RecordAsync(siteId, thisMonth, 4 * OneGibibyte, CancellationToken.None);
        await meter.RecordAsync(siteId, nextMonth, 2 * OneGibibyte, CancellationToken.None);

        await using (var db = fixture.CreateDbContext())
        {
            var charge = DownloadOverageCharge.PendingCheckout(
                new DownloadOverageChargeId(Guid.NewGuid()), siteId, thisMonth,
                bytesOver: OneGibibyte, amountRub: 100m, priceVersion: 1,
                yooKassaPaymentId: "pmt_" + Guid.NewGuid().ToString("N"), createdAt: Now);
            charge.MarkSucceeded(Now);
            await new DownloadOverageChargeRepository(db).SaveAsync(charge, CancellationToken.None);
        }

        var store = new DownloadOverageReadStore(fixture.DataSource);

        var settlement = await store.GetSettlementAsync(siteId, thisMonth, CancellationToken.None);
        Assert.Equal(OneGibibyte, settlement.SettledBytes);
        Assert.Equal(100m, settlement.SettledAmountRub);
        Assert.True(settlement.HasPaidCheckout);

        var outstanding = await store.GetOutstandingAsync(siteId, OneGibibyte, nextMonth, CancellationToken.None);
        Assert.Equal(2, outstanding.Count);
        // This month: 4 GiB total, 1 GiB threshold, 1 GiB already settled -> 2 GiB outstanding.
        Assert.Equal(thisMonth, outstanding[0].PeriodMonth);
        Assert.Equal(2 * OneGibibyte, outstanding[0].OutstandingBytes);
        Assert.True(outstanding[0].HasPaidCheckout);
        // Next month: 2 GiB total, 1 GiB threshold, nothing settled -> 1 GiB outstanding, unpaid.
        Assert.Equal(nextMonth, outstanding[1].PeriodMonth);
        Assert.Equal(OneGibibyte, outstanding[1].OutstandingBytes);
        Assert.False(outstanding[1].HasPaidCheckout);
    }

    /// <summary>The migration's own seeded `v1` row is real, readable and exactly 100 RUB -
    /// `docs/backlog/25-84-*.md`'s own first Done-when ("100 RUB as the shipped default"), checked
    /// against the database rather than against the constant a test could have retyped.</summary>
    [Fact]
    public async Task TheShippedOveragePrice_Is100Rub_AndIsOwnerRepublishable()
    {
        await using var db = fixture.CreateDbContext();
        var prices = new PriceCatalogRepository(db);

        var current = await prices.FindCurrentAsync(DownloadOveragePricing.OveragePerGigabyteKey, CancellationToken.None);

        Assert.NotNull(current);
        Assert.Equal(100.00m, current.AmountRub);
        // Registered, which is the only thing that lets the platform owner publish a new version for it
        // (`PublishPriceVersionHandler` checks exactly this).
        Assert.True(PricedResourceKeys.IsKnown(DownloadOveragePricing.OveragePerGigabyteKey));
    }

    private async Task SetBillingModeAsync(SiteId siteId, DownloadOverageBillingMode mode)
    {
        await using var db = fixture.CreateDbContext();
        var repository = new SiteRepository(db);
        var site = await repository.GetByIdAsync(siteId, CancellationToken.None);
        site!.SetDownloadOverageBillingMode(mode, "owner-test", "proving the paid path", Now);
        await repository.SaveAsync(site, CancellationToken.None);
    }

    /// <summary>`25-84`'s own checkout needs `site:configure`, not the `conversation:read`
    /// <see cref="SeedSiteWithConversationReadPermissionAndThresholdAsync"/> grants - added to the same
    /// role rather than a second one, so the operator is the same person throughout the test.</summary>
    private async Task GrantSiteConfigureAsync(SiteId siteId, OperatorId operatorId)
    {
        await using var db = fixture.CreateDbContext();
        var roleId = Guid.NewGuid();
        db.Roles.Add(new RoleRecord
        {
            Id = roleId,
            SiteId = siteId,
            Name = "BillingAdmin",
            Permissions = [Permission.SiteConfigure.Value],
        });
        db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
        await db.SaveChangesAsync();
    }

    /// <summary>`25-84`: records whichever payment id this test wants the webhook to quote back, so the
    /// applier can be driven without a real ЮKassa. The same "a fake at the port, real everything below
    /// it" split every other integration test in this file uses for `IFileStorage`.</summary>
    private sealed class RecordingYooKassaClient(string paymentId) : IYooKassaPaymentsClient
    {
        public string PaymentId { get; } = paymentId;

        public Task<CreatePaymentResult> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<CreatePaymentResult>(
                new CreatePaymentResult.Success(PaymentId, "https://yookassa.example/confirm/" + PaymentId));

        public Task<ChargeStoredPaymentMethodResult> ChargeStoredPaymentMethodAsync(
            ChargeStoredPaymentMethodRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("`25-84`'s own checkout never charges a stored method - see PurchaseDownloadOverageHandler's own remarks.");
    }

    private sealed class NoOpOutboxWriter : IOutboxWriter
    {
        public void Enqueue(EventEnvelope envelope, string? traceContext = null)
        {
        }
    }
}
