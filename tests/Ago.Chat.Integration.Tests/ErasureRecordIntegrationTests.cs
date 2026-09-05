using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Caching;
using Ago.Chat.Application.UseCases.RequestConversationErasure;
using Ago.Chat.Application.UseCases.RequestSiteErasure;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Keycloak;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Worker;
using Ago.Platform.Abstractions;
using Ago.Platform.Caching.Redis;
using Ago.Platform.Kernel;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `24-13`'s own Done-when, against a real Postgres and a real MinIO (`ErasureFixture`, the same
/// fixture <see cref="ConversationErasureIntegrationTests"/>/<see cref="SiteErasureIntegrationTests"/>
/// use): a completed erasure leaves a receipt with real counts; a failed one leaves a receipt saying
/// so, not silence; and the receipt holds nothing that could single out the person it was about -
/// proven positively, over the actual persisted row, not by reading <see cref="ErasureRecordEntity"/>'s
/// own shape (a column added later without updating that type's own remarks would pass a shape-based
/// test and still leak; it cannot pass a test that inspects the value that actually landed in the
/// row).
/// </summary>
[Collection(ErasureCollection.Name)]
public class ErasureRecordIntegrationTests(ErasureFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 9, 0, 0, TimeSpan.Zero);

    // The exact, ordered column set `erasure_records` is allowed to have - a positive assertion,
    // not merely "these forbidden names are absent". A future column added without updating this list
    // fails this test immediately, which is the point: this table's shape is a decision, not a
    // side effect of whatever the next migration happens to add.
    private static readonly string[] ExpectedColumns =
    [
        "attachments_deleted",
        "completed_at",
        "contact_details_deleted",
        "conversations_marked_for_erasure",
        "failure_reason",
        "id",
        "identities_deleted",
        "messages_deleted",
        "notes_deleted",
        "requested_at",
        "requested_by",
        "scope",
        "site_id",
        "status",
        "storage_objects_deleted",
        "tags_deleted",
    ];

    private sealed class SettableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    [Fact]
    public async Task ErasureRecords_HasExactlyTheColumnsThisItemDecidedOn()
    {
        var columns = (await QueryColumnsAsync()).OrderBy(c => c, StringComparer.Ordinal).ToArray();
        Assert.Equal(ExpectedColumns, columns);
    }

    [Fact]
    public async Task ErasingOneConversation_LeavesACompletedReceiptWithRealCounts_AndNoIdentifierOfWhoWasErased()
    {
        var clock = new SettableClock(Now);
        var siteId = await SeedSiteAsync("erasure-record-site-1");
        var (adminOperatorId, _) = await SeedOperatorAsync(siteId);
        var toErase = await SeedConversationWithAttachmentAsync(siteId);

        var tagId = await SeedTagAsync(siteId, "vip");
        await TagConversationAsync(toErase.ConversationId, tagId);
        await SeedNoteAsync(toErase.ConversationId, adminOperatorId, "note");
        await SeedContactDetailAsync(toErase.VisitorId, adminOperatorId, "+7 000 000-00-09");

        var erasureRequests = new ErasureRequestRepository(fixture.DataSource);
        await using (var permissionDb = fixture.CreateDbContext())
        {
            var requestHandler = new RequestConversationErasureHandler(
                erasureRequests, new PermissionChecker(permissionDb), new UuidV7Generator(), clock);
            var requested = await requestHandler.HandleAsync(
                new RequestConversationErasure(toErase.ConversationId, adminOperatorId, siteId), CancellationToken.None);
            Assert.True(requested.IsSuccess, requested.IsFailure ? requested.Error!.Value.ToString() : null);
        }

        var conversationJob = CreateConversationJob(clock);
        for (var i = 0; i < 3; i++)
        {
            await conversationJob.SweepAsync(CancellationToken.None);
        }

        // The conversation is genuinely gone - the ordinary Done-when ConversationErasureIntegrationTests
        // already proves; this test's own subject is the receipt left behind, not the deletion itself.
        Assert.Equal(0, await CountAsync("select count(*) from conversations where id = @id", toErase.ConversationId.Value));

        var record = await QuerySingleErasureRecordAsync(siteId.Value);

        Assert.Equal("Conversation", (string)record.scope);
        Assert.Equal("Completed", (string)record.status);
        Assert.Equal(adminOperatorId.Value, (Guid)record.requested_by);
        Assert.Equal(siteId.Value, (Guid)record.site_id);
        Assert.Null((string?)record.failure_reason);
        Assert.NotNull(record.completed_at);

        // Real per-step counts, not a placeholder: two messages, one attachment row, two storage
        // objects (the attachment and its thumbnail), one note, one tag association, one contact
        // detail - exactly what SeedConversationWithAttachmentAsync/the seeding above put there.
        Assert.Equal(2, (int)record.messages_deleted);
        Assert.Equal(1, (int)record.attachments_deleted);
        Assert.Equal(2, (int)record.storage_objects_deleted);
        Assert.Equal(1, (int)record.notes_deleted);
        Assert.Equal(1, (int)record.tags_deleted);
        Assert.Equal(1, (int)record.contact_details_deleted);

        // `24-13`'s own load-bearing assertion: no field of this row, read back as it was actually
        // persisted, names the erased conversation or its visitor - checked positively, over the real
        // values, not by trusting that no column was declared for them.
        AssertRowNamesNeither(record, toErase.ConversationId.Value, toErase.VisitorId.Value);
    }

    /// <summary>
    /// `24-13`'s second fails-before case: an erasure that dies partway through must leave a record
    /// saying so, with whatever it actually finished, not silence. The failure is real, not simulated
    /// through a double - `ConversationArchiveEraser`'s own remarks say a storage read/parse failure is
    /// "allowed to throw rather than being logged and tolerated", so a `message_archives` row pointing
    /// at an object that is not a valid archive (real bytes in real MinIO, just not a zip) reaches
    /// exactly that documented throw, after the message batch has already been deleted for real -
    /// which is what lets this test assert the receipt's own `messages_deleted` reflects genuine,
    /// completed work rather than being reset to zero by the failure.
    /// </summary>
    [Fact]
    public async Task ErasingOneConversation_WhenTheArchiveStepThrows_LeavesAFailedReceiptWithWhateverItFinished()
    {
        var clock = new SettableClock(Now);
        var siteId = await SeedSiteAsync("erasure-record-site-2");
        var (adminOperatorId, _) = await SeedOperatorAsync(siteId);
        var toErase = await SeedConversationWithAttachmentAsync(siteId);

        // A real object in real MinIO, at the key a message_archives row will name - not a valid zip,
        // so ConversationArchiveEraser's own ZipFile.OpenRead throws InvalidDataException once it
        // downloads real bytes rather than getting a 404 (which this class tolerates as "already gone").
        var corruptArchiveKey = $"archive/messages/{siteId.Value}/free/2026-09.zip";
        await fixture.UploadTestObjectAsync(corruptArchiveKey, "not a zip file"u8.ToArray(), "application/zip");
        await SeedArchiveManifestRowAsync(siteId, corruptArchiveKey);

        var erasureRequests = new ErasureRequestRepository(fixture.DataSource);
        await using (var permissionDb = fixture.CreateDbContext())
        {
            var requestHandler = new RequestConversationErasureHandler(
                erasureRequests, new PermissionChecker(permissionDb), new UuidV7Generator(), clock);
            var requested = await requestHandler.HandleAsync(
                new RequestConversationErasure(toErase.ConversationId, adminOperatorId, siteId), CancellationToken.None);
            Assert.True(requested.IsSuccess, requested.IsFailure ? requested.Error!.Value.ToString() : null);
        }

        var conversationJob = CreateConversationJob(clock);
        // One cycle only - SweepAsync's own per-item catch logs and swallows the throw, so this does
        // not fail the test; the conversation stays flagged (erasure_requested_at survives), exactly
        // the retry-next-cycle behaviour ConversationErasureJob's own remarks describe.
        await conversationJob.SweepAsync(CancellationToken.None);

        // Not erased - the archive step never let EraseConversationAsync reach DeleteConversationAsync.
        Assert.Equal(1, await CountAsync("select count(*) from conversations where id = @id", toErase.ConversationId.Value));

        var record = await QuerySingleErasureRecordAsync(siteId.Value);

        Assert.Equal("Failed", (string)record.status);
        Assert.Equal("InvalidDataException", (string)record.failure_reason);
        Assert.NotNull(record.completed_at);
        // The messages were genuinely deleted this cycle, before the archive step ran - the receipt
        // must say so, not report zero just because the cycle as a whole did not finish.
        Assert.Equal(2, (int)record.messages_deleted);
        // Not yet recorded: CompleteConversationErasureAsync (the call that would set these) never ran,
        // because the throw happened before it - this is this item's own documented ordering
        // (ErasureRecordQuery's own remarks), not an omission.
        Assert.Equal(0, (int)record.attachments_deleted);

        AssertRowNamesNeither(record, toErase.ConversationId.Value, toErase.VisitorId.Value);
    }

    /// <summary>
    /// `24-13`'s scoping decision, locked in by a test: a conversation swept up by a whole-site erasure
    /// (rather than named in its own standalone request) gets no `erasure_records` row of its own - the
    /// site's own receipt is what proves that erasure, via <c>conversations_marked_for_erasure</c>, not
    /// a receipt per conversation. Two conversations, one site erasure, one resulting row.
    /// </summary>
    [Fact]
    public async Task ErasingASite_LeavesExactlyOneReceipt_NotOnePerConversationItDrains()
    {
        var clock = new SettableClock(Now);
        var siteId = await SeedSiteAsync("erasure-record-site-3");
        var (adminOperatorId, subjectId) = await SeedOperatorAsync(siteId);
        var first = await SeedConversationAsync(siteId);
        var second = await SeedConversationAsync(siteId);

        var erasureRequests = new ErasureRequestRepository(fixture.DataSource);
        await using (var permissionDb = fixture.CreateDbContext())
        {
            var requestHandler = new RequestSiteErasureHandler(
                erasureRequests, new PermissionChecker(permissionDb), new UuidV7Generator(), clock);
            var requested = await requestHandler.HandleAsync(
                new RequestSiteErasure(siteId, adminOperatorId), CancellationToken.None);
            Assert.True(requested.IsSuccess, requested.IsFailure ? requested.Error!.Value.ToString() : null);
        }

        var recordingPublisher = new RecordingEventPublisher();
        var cacheInvalidation = new CacheInvalidationPublisher(recordingPublisher, clock);
        var provisioner = CreateProvisioner();
        var conversationJob = CreateConversationJob(clock);
        var siteJob = CreateSiteJob(clock, provisioner, cacheInvalidation);

        for (var i = 0; i < 5; i++)
        {
            await conversationJob.SweepAsync(CancellationToken.None);
            await siteJob.SweepAsync(CancellationToken.None);
        }

        Assert.Equal(0, await CountAsync("select count(*) from sites where id = @id", siteId.Value));
        Assert.Equal(
            0, await CountAsync("select count(*) from erasure_records where site_id = @id and scope = 'Conversation'", siteId.Value));

        var record = await QuerySingleErasureRecordAsync(siteId.Value);
        Assert.Equal("Site", (string)record.scope);
        Assert.Equal("Completed", (string)record.status);
        Assert.Equal(adminOperatorId.Value, (Guid)record.requested_by);
        Assert.Equal(2, (int)record.conversations_marked_for_erasure);
        Assert.Equal(1, (int)record.identities_deleted);

        AssertRowNamesNeither(record, first.ConversationId.Value, first.VisitorId.Value);
        AssertRowNamesNeither(record, second.ConversationId.Value, second.VisitorId.Value);
        _ = subjectId;
    }

    private static void AssertRowNamesNeither(dynamic record, Guid conversationId, Guid visitorId)
    {
        IDictionary<string, object> values = record;
        var serialized = string.Join('|', values.Values.Select(v => v?.ToString() ?? string.Empty));
        Assert.DoesNotContain(conversationId.ToString(), serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(visitorId.ToString(), serialized, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<IEnumerable<string>> QueryColumnsAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        return await connection.QueryAsync<string>(
            "select column_name from information_schema.columns where table_name = 'erasure_records'");
    }

    private async Task<dynamic> QuerySingleErasureRecordAsync(Guid siteId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync(
            "select * from erasure_records where site_id = @siteId", new { siteId });
    }

    // Raw Npgsql, not Dapper - Dapper has no built-in DateOnly handler
    // (MessageRetentionArchiveEndToEndTests's own remarks), and production's own
    // MessageArchiveRepository is raw Npgsql end to end anyway for period_start/period_end.
    private async Task SeedArchiveManifestRowAsync(SiteId siteId, string objectKey)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            insert into message_archives (id, site_id, retention_class, period_start, period_end, object_key, archived_at)
            values (@id, @siteId, @retentionClass, @periodStart, @periodEnd, @objectKey, @archivedAt)
            """,
            connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("siteId", siteId.Value);
        command.Parameters.AddWithValue("retentionClass", RetentionClass.Free.Value);
        command.Parameters.AddWithValue("periodStart", new DateOnly(2026, 9, 1));
        command.Parameters.AddWithValue("periodEnd", new DateOnly(2026, 9, 30));
        command.Parameters.AddWithValue("objectKey", objectKey);
        command.Parameters.AddWithValue("archivedAt", Now);
        await command.ExecuteNonQueryAsync();
    }

    private ConversationErasureJob CreateConversationJob(IClock clock)
    {
        var erasureOptions = new ConversationErasureJobOptions();
        var archiveEraser = new ConversationArchiveEraser(
            fixture.FileStorage, new MessageArchiveRepository(fixture.DataSource), erasureOptions,
            NullLogger<ConversationArchiveEraser>.Instance);
        return new ConversationErasureJob(
            fixture.DataSource, fixture.FileStorage, archiveEraser, clock,
            Options.Create(erasureOptions), NullLogger<ConversationErasureJob>.Instance);
    }

    private SiteErasureJob CreateSiteJob(IClock clock, IDemoIdentityProvisioner identities, CacheInvalidationPublisher cacheInvalidation) =>
        new(fixture.DataSource, identities, fixture.FileStorage, new MessageArchiveRepository(fixture.DataSource),
            cacheInvalidation, new UuidV7Generator(), clock,
            Options.Create(new SiteErasureJobOptions()), NullLogger<SiteErasureJob>.Instance);

    private KeycloakDemoIdentityProvisioner CreateProvisioner() =>
        new(
            new HttpClient(),
            new KeycloakAdminOptions
            {
                BaseUrl = fixture.KeycloakBaseUrl,
                Realm = ErasureFixture.RealmName,
                ClientId = ErasureFixture.ProvisionerClientId,
                ClientSecret = ErasureFixture.ProvisionerClientSecret,
            },
            new SettableClock(Now),
            NullLogger<KeycloakDemoIdentityProvisioner>.Instance);

    private async Task<SiteId> SeedSiteAsync(string name)
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", ["https://shop.example"], name));
        await db.SaveChangesAsync();
        return siteId;
    }

    private async Task<(OperatorId OperatorId, string SubjectId)> SeedOperatorAsync(SiteId siteId)
    {
        var subjectId = await fixture.CreateOperatorUserAsync($"erasure-rec-op-{Guid.NewGuid():N}"[..24]);
        var operatorId = new OperatorId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Offline, capacity: 5, externalSubjectId: subjectId));
        var roleId = Guid.NewGuid();
        db.Roles.Add(new RoleRecord
        {
            Id = roleId,
            SiteId = siteId,
            Name = "Admin",
            Permissions = [Permission.SiteErase.Value, Permission.ConversationErase.Value],
        });
        db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
        await db.SaveChangesAsync();

        return (operatorId, subjectId);
    }

    private async Task<(ConversationId ConversationId, VisitorId VisitorId)> SeedConversationAsync(SiteId siteId)
    {
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, Now);

        await using var db = fixture.CreateDbContext();
        db.Visitors.Add(new Visitor(visitorId, siteId, Now));
        await db.SaveChangesAsync();
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();

        return (conversation.Id, visitorId);
    }

    private async Task<(ConversationId ConversationId, VisitorId VisitorId, string ObjectKey, string ThumbnailKey)> SeedConversationWithAttachmentAsync(SiteId siteId)
    {
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, Now);
        conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("hello"), Now);
        conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("still there?"), Now);

        await using (var db = fixture.CreateDbContext())
        {
            db.Visitors.Add(new Visitor(visitorId, siteId, Now));
            await db.SaveChangesAsync();
            db.Conversations.Add(conversation);
            await db.SaveChangesAsync();
        }

        var objectKey = $"site/{siteId.Value}/conv/{conversation.Id.Value}/{Guid.NewGuid():N}.png";
        var thumbnailKey = $"site/{siteId.Value}/conv/{conversation.Id.Value}/{Guid.NewGuid():N}.thumb.jpg";
        await fixture.UploadTestObjectAsync(objectKey, [1, 2, 3, 4], "image/png");
        await fixture.UploadTestObjectAsync(thumbnailKey, [5, 6, 7, 8], "image/jpeg");

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            insert into attachments
                (id, site_id, conversation_id, object_key, content_type, size_bytes, state, created_at, thumbnail_key)
            values (@id, @siteId, @conversationId, @objectKey, 'image/png', 4, 'Ready', now(), @thumbnailKey)
            """,
            new
            {
                id = Guid.NewGuid(),
                siteId = siteId.Value,
                conversationId = conversation.Id.Value,
                objectKey,
                thumbnailKey,
            });

        return (conversation.Id, visitorId, objectKey, thumbnailKey);
    }

    private async Task SeedContactDetailAsync(VisitorId visitorId, OperatorId recordedByOperatorId, string value)
    {
        var detail = VisitorContactDetail.Record(
            new VisitorContactDetailId(Guid.NewGuid()), visitorId, VisitorContactDetailKind.Phone, value,
            recordedByOperatorId, Now);
        await using var db = fixture.CreateDbContext();
        await new VisitorContactDetailRepository(db).SaveAsync(detail, CancellationToken.None);
    }

    private async Task<TagId> SeedTagAsync(SiteId siteId, string name)
    {
        var tag = Tag.Create(new TagId(Guid.NewGuid()), siteId, name, Now);
        await using var db = fixture.CreateDbContext();
        await new TagRepository(db).SaveAsync(tag, CancellationToken.None);
        return tag.Id;
    }

    private async Task TagConversationAsync(ConversationId conversationId, TagId tagId)
    {
        await using var db = fixture.CreateDbContext();
        await new TagRepository(db).AddToConversationAsync(conversationId, tagId, TagSource.Operator, CancellationToken.None);
    }

    private async Task SeedNoteAsync(ConversationId conversationId, OperatorId authorId, string body)
    {
        var note = ConversationNote.Write(new ConversationNoteId(Guid.NewGuid()), conversationId, authorId, body, Now);
        await using var db = fixture.CreateDbContext();
        await new NoteRepository(db).SaveAsync(note, CancellationToken.None);
    }

    private async Task<int> CountAsync(string sql, Guid id)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(sql, new { id });
    }
}
