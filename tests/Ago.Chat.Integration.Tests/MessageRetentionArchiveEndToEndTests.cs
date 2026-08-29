using System.IO.Compression;
using System.Text.Json;
using Ago.Chat.Application.UseCases.GetMessageArchiveDownloadUrl;
using Ago.Chat.Application.UseCases.ListMessageArchives;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Worker;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `13-06`/`adr/0031`'s own Done-when, live, against a real Postgres and a real MinIO
/// (<see cref="AttachmentFixture"/>, the same combination <see cref="SiteExportIntegrationTests"/>
/// already uses): a period is archived to object storage and only then dropped
/// (<see cref="ArchiveThenPrune_ArchivesUploadsAndDropsThePartition_AndSweepsItsAttachments"/>), a
/// failed archive upload provably leaves the partition undropped
/// (<see cref="WhenTheArchiveUploadFails_TheGateRefusesToConfirm_AndTheDropDoesNotHappen"/>), a tenant
/// can request and receive an archived period end to end
/// (<see cref="ArchiveThenPrune_ArchivesUploadsAndDropsThePartition_AndSweepsItsAttachments"/> also
/// drives the retrieval read), and <see cref="RetentionClassImmutabilityTests"/> proves a tier change
/// never touches an already-written row's class.
///
/// <para>Every partition this suite creates is far in the past (year 2010) so it can never collide
/// with <see cref="PartitionMaintenanceJob"/>'s own ongoing current-month activity in this shared
/// fixture - <see cref="MessagePartitionPruneJobTests"/>'s own convention (1999-2001), a distinct year
/// here since this suite shares no fixture with that one but the collision risk is identical in
/// kind.</para>
/// </summary>
[Collection(AttachmentCollection.Name)]
public sealed class MessageRetentionArchiveEndToEndTests(AttachmentFixture fixture)
{
    // Dapper has no built-in DateOnly handler (unlike raw Npgsql, which every production repository in
    // this codebase uses instead - MessageArchiveRepository's own AddWithValue calls never hit this).
    // This test's own CountAsync helper is the one place in this file that goes through Dapper with a
    // DateOnly parameter, so it is the one place that needs this registered.
    static MessageRetentionArchiveEndToEndTests() => SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());

    private static readonly DateTimeOffset ReferenceNow = new(2010, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly HttpClient Http = new();
    private const int RetentionHorizonMonths = 3;

    /// <summary>The full happy path: archive, confirm, drop, sweep, retrieve.</summary>
    [Fact]
    public async Task ArchiveThenPrune_ArchivesUploadsAndDropsThePartition_AndSweepsItsAttachments()
    {
        var retentionClass = RetentionClass.Free;
        var partitionName = await CreatePartitionAsync(retentionClass, 2010, 1);
        try
        {
            var (siteId, operatorId) = await SeedSiteAndOperatorAsync("archive-e2e-site");
            var conversationId = await SeedConversationAsync(siteId);
            var periodStart = new DateOnly(2010, 1, 1);
            var createdAt = new DateTimeOffset(2010, 1, 15, 12, 0, 0, TimeSpan.Zero);

            var messageId = await SeedMessageAsync(conversationId, retentionClass, siteId, createdAt, "archive me", attachmentId: null);
            // Uploads real bytes to MinIO, inserts the attachments row, and links it back onto the
            // message just seeded above - bypassing ConfirmAttachmentHandler (this suite seeds state
            // directly, the same convention SiteExportIntegrationTests' own SeedAttachmentAsync
            // establishes).
            var (attachmentId, attachmentBytes, attachmentObjectKey) =
                await LinkAttachmentToMessageAsync(siteId, conversationId, messageId, createdAt);

            var clock = new SettableClock(ReferenceNow);
            var archiveJob = CreateArchiveJob(clock, fixture.FileStorage);
            var archived = await archiveJob.ArchiveAsync(CancellationToken.None);
            Assert.Equal(1, archived);

            // The manifest row exists, and the object it points at is real.
            Assert.Equal(1, await CountAsync(
                "select count(*) from message_archives where site_id = @siteId and retention_class = @class and period_start = @periodStart",
                new { siteId = siteId.Value, @class = retentionClass.Value, periodStart }));

            // Retrieval, end to end: list then download.
            var listHandler = new ListMessageArchivesHandler(new MessageArchiveRepository(fixture.DataSource), new PermissionChecker(fixture.CreateDbContext()));
            var listed = await listHandler.HandleAsync(new ListMessageArchives(siteId, operatorId), CancellationToken.None);
            Assert.True(listed.IsSuccess);
            var period = Assert.Single(listed.Value);
            Assert.Equal(retentionClass.Value, period.RetentionClass.Value);
            Assert.Equal(periodStart, period.PeriodStart);

            var downloadHandler = new GetMessageArchiveDownloadUrlHandler(
                new MessageArchiveRepository(fixture.DataSource), fixture.FileStorage,
                new PermissionChecker(fixture.CreateDbContext()), new MessageArchiveOptions());
            var download = await downloadHandler.HandleAsync(
                new GetMessageArchiveDownloadUrl(siteId, retentionClass, periodStart, operatorId), CancellationToken.None);
            Assert.True(download.IsSuccess);

            using var archiveResponse = await Http.GetAsync(download.Value);
            archiveResponse.EnsureSuccessStatusCode();
            using var archiveStream = new MemoryStream(await archiveResponse.Content.ReadAsByteArrayAsync());
            using var zip = new ZipArchive(archiveStream, ZipArchiveMode.Read);

            var manifest = await ReadJsonAsync(zip, "manifest.json");
            Assert.Equal(siteId.Value, manifest.GetProperty("siteId").GetGuid());
            Assert.Equal(retentionClass.Value, manifest.GetProperty("retentionClass").GetString());

            var messageRows = await ReadJsonLinesAsync(zip, "messages.jsonl");
            var messageRow = Assert.Single(messageRows);
            Assert.Equal("archive me", messageRow.GetProperty("body").GetString());

            var attachmentRows = await ReadJsonLinesAsync(zip, "attachments.jsonl");
            var attachmentRow = Assert.Single(attachmentRows);
            var downloadUrl = attachmentRow.GetProperty("downloadUrl").GetString();
            using var attachmentResponse = await Http.GetAsync(downloadUrl);
            attachmentResponse.EnsureSuccessStatusCode();
            Assert.Equal(attachmentBytes, await attachmentResponse.Content.ReadAsByteArrayAsync());

            // Now the drop: MessagePartitionPruneJob's real gate confirms (every distinct site in this
            // partition now has a matching manifest row) and the partition genuinely disappears.
            var pruneJob = CreatePruneJob(clock, fixture.FileStorage, new MessageArchiveGate(fixture.DataSource));
            await pruneJob.PruneAsync(CancellationToken.None);
            Assert.False(await PartitionExistsAsync(partitionName));

            // "Attachments and thumbnails for an expired period are gone": the row and the MinIO
            // object both disappeared as a direct consequence of the drop.
            Assert.Equal(0, await CountAsync("select count(*) from attachments where id = @id", new { id = attachmentId }));
            Assert.Null(await fixture.FileStorage.GetMetadataAsync(new ObjectKey(attachmentObjectKey), CancellationToken.None));
        }
        finally
        {
            await DropIfExistsAsync(partitionName);
        }
    }

    /// <summary>The single most important proof in this item: an archive upload failure must leave the
    /// partition exactly as it was - not partially archived, not dropped. <see cref="ThrowingOnUploadFileStorage"/>
    /// wraps the real MinIO-backed <see cref="IFileStorage"/> and fails only the one call
    /// <see cref="MessageArchiveJob"/> makes to actually write the object, so this is a real Postgres and
    /// a real (fake-failure-injected, real-protocol) object storage throughout - not a mock asserting
    /// call order.</summary>
    [Fact]
    public async Task WhenTheArchiveUploadFails_TheGateRefusesToConfirm_AndTheDropDoesNotHappen()
    {
        var retentionClass = RetentionClass.Free;
        var partitionName = await CreatePartitionAsync(retentionClass, 2010, 2);
        try
        {
            var (siteId, _) = await SeedSiteAndOperatorAsync("archive-fail-site");
            var conversationId = await SeedConversationAsync(siteId);
            var createdAt = new DateTimeOffset(2010, 2, 15, 12, 0, 0, TimeSpan.Zero);
            await SeedMessageAsync(conversationId, retentionClass, siteId, createdAt, "never archived", attachmentId: null);

            var clock = new SettableClock(ReferenceNow);
            var failingStorage = new ThrowingOnUploadFileStorage(fixture.FileStorage);
            var archiveJob = CreateArchiveJob(clock, failingStorage);

            // The job must not throw out of ArchiveAsync itself (one site's failure must not sink the
            // cycle) - it catches, logs, and reports zero successes.
            var archived = await archiveJob.ArchiveAsync(CancellationToken.None);
            Assert.Equal(0, archived);
            Assert.True(failingStorage.UploadWasAttempted);

            // No manifest row - the upload never completed, so IMessageArchiveRepository.RecordAsync
            // was never reached (MessageArchiveJob's own ordering: record only after upload succeeds).
            Assert.Equal(0, await CountAsync(
                "select count(*) from message_archives where site_id = @siteId", new { siteId = siteId.Value }));

            // The gate itself, asked directly, refuses to confirm this partition.
            var gate = new MessageArchiveGate(fixture.DataSource);
            var confirmed = await gate.IsArchivedAsync(partitionName, new DateOnly(2010, 2, 1), new DateOnly(2010, 3, 1), CancellationToken.None);
            Assert.False(confirmed);

            // And MessagePartitionPruneJob, driven for real, leaves the partition exactly where it was.
            var pruneJob = CreatePruneJob(clock, fixture.FileStorage, gate);
            await pruneJob.PruneAsync(CancellationToken.None);
            Assert.True(await PartitionExistsAsync(partitionName));
        }
        finally
        {
            await DropIfExistsAsync(partitionName);
        }
    }

    private MessageArchiveJob CreateArchiveJob(IClock clock, IFileStorage fileStorage)
    {
        var archiveOptions = new MessageArchiveJobOptions { AttachmentUrlLifetime = TimeSpan.FromMinutes(30) };
        var writer = new MessageArchiveWriter(fileStorage, archiveOptions);
        return new MessageArchiveJob(
            fixture.DataSource, fileStorage, new MessageArchiveRepository(fixture.DataSource), writer, clock, new UuidV7Generator(),
            Options.Create(new MessagePartitionPruneJobOptions { RetentionHorizonMonths = RetentionHorizonMonths }),
            Options.Create(archiveOptions), NullLogger<MessageArchiveJob>.Instance);
    }

    private MessagePartitionPruneJob CreatePruneJob(IClock clock, IFileStorage fileStorage, Application.Abstractions.IMessageArchiveGate gate) =>
        new(fixture.DataSource, gate, fileStorage, clock,
            Options.Create(new MessagePartitionPruneJobOptions { RetentionHorizonMonths = RetentionHorizonMonths }),
            NullLogger<MessagePartitionPruneJob>.Instance);

    private async Task<string> CreatePartitionAsync(RetentionClass retentionClass, int year, int month)
    {
        var from = new DateOnly(year, month, 1);
        var name = MessagePartitionNames.ForMonth(retentionClass, new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync($"""
            CREATE TABLE IF NOT EXISTS {name} PARTITION OF {MessagePartitionNames.ForClass(retentionClass)}
                FOR VALUES FROM ('{from:yyyy-MM-dd}') TO ('{from.AddMonths(1):yyyy-MM-dd}');
            """);
        return name;
    }

    private async Task<(SiteId SiteId, OperatorId OperatorId)> SeedSiteAndOperatorAsync(string name)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", ["https://shop.example"], name));
        db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Offline, capacity: 5, externalSubjectId: $"subject-{operatorId.Value:N}"));
        var roleId = Guid.NewGuid();
        db.Roles.Add(new RoleRecord { Id = roleId, SiteId = siteId, Name = "Admin", Permissions = [Permission.SiteExport.Value] });
        db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
        await db.SaveChangesAsync();
        return (siteId, operatorId);
    }

    private async Task<ConversationId> SeedConversationAsync(SiteId siteId)
    {
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, ReferenceNow);
        await using var db = fixture.CreateDbContext();
        db.Visitors.Add(new Visitor(visitorId, siteId, ReferenceNow));
        await db.SaveChangesAsync();
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();
        return conversation.Id;
    }

    /// <summary>Bypasses `Conversation`/EF entirely - direct SQL into a partition this test built by
    /// hand, matching every other far-past-partition seed in this codebase's own test suites
    /// (`MessagePartitionPruneJobTests`'s own convention). The row lands via Postgres's own partition
    /// routing on `(retention_class, created_at)` - no partition name is needed as an argument.</summary>
    private async Task<Guid> SeedMessageAsync(
        ConversationId conversationId, RetentionClass retentionClass, SiteId siteId,
        DateTimeOffset createdAt, string body, Guid? attachmentId)
    {
        var messageId = Guid.NewGuid();
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            insert into messages (id, conversation_id, sequence, author_kind, author_id, body, created_at, retention_class, site_id, attachment_id)
            values (@id, @conversationId, 1, 'Visitor', @authorId, @body, @createdAt, @retentionClass, @siteId, @attachmentId)
            """,
            new
            {
                id = messageId,
                conversationId = conversationId.Value,
                authorId = Guid.NewGuid(),
                body,
                createdAt,
                retentionClass = retentionClass.Value,
                siteId = siteId.Value,
                attachmentId,
            });
        return messageId;
    }

    /// <summary>Uploads real bytes to MinIO, inserts the `attachments` row, and links it back onto
    /// <paramref name="messageId"/> - bypassing `ConfirmAttachmentHandler`, the same direct-seed
    /// convention <see cref="SeedMessageAsync"/> uses for the message itself.</summary>
    private async Task<(Guid AttachmentId, byte[] Bytes, string ObjectKey)> LinkAttachmentToMessageAsync(
        SiteId siteId, ConversationId conversationId, Guid messageId, DateTimeOffset createdAt)
    {
        byte[] bytes = [9, 8, 7, 6, 5];
        var objectKey = $"site/{siteId.Value}/conv/{conversationId.Value}/{Guid.NewGuid():N}.png";
        var presigned = await fixture.FileStorage.CreateUploadAsync(
            new ObjectKey(objectKey), new UploadConstraints("image/png", bytes.Length, TimeSpan.FromMinutes(5)), CancellationToken.None);
        using (var content = new ByteArrayContent(bytes))
        {
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            using var response = await Http.PutAsync(presigned.Url, content);
            response.EnsureSuccessStatusCode();
        }

        var attachmentId = Guid.NewGuid();
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            insert into attachments (id, site_id, conversation_id, message_id, object_key, content_type, size_bytes, state, created_at)
            values (@id, @siteId, @conversationId, @messageId, @objectKey, 'image/png', @sizeBytes, 'Ready', @createdAt)
            """,
            new
            {
                id = attachmentId,
                siteId = siteId.Value,
                conversationId = conversationId.Value,
                messageId,
                objectKey,
                sizeBytes = (long)bytes.Length,
                createdAt,
            });
        await connection.ExecuteAsync(
            "update messages set attachment_id = @attachmentId where id = @messageId", new { attachmentId, messageId });

        return (attachmentId, bytes, objectKey);
    }

    private async Task<bool> PartitionExistsAsync(string partitionName) =>
        await CountAsync("select count(*) from pg_class where relname = @name", new { name = partitionName }) > 0;

    private async Task DropIfExistsAsync(string partitionName)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync($"DROP TABLE IF EXISTS {partitionName};");
    }

    private async Task<int> CountAsync(string sql, object parameters)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(sql, parameters);
    }

    private static async Task<JsonElement> ReadJsonAsync(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName) ?? throw new InvalidOperationException($"Archive has no entry '{entryName}'.");
        await using var stream = entry.Open();
        using var document = await JsonDocument.ParseAsync(stream);
        return document.RootElement.Clone();
    }

    private static async Task<IReadOnlyList<JsonElement>> ReadJsonLinesAsync(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName) ?? throw new InvalidOperationException($"Archive has no entry '{entryName}'.");
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        var lines = new List<JsonElement>();
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            if (line.Length == 0)
            {
                continue;
            }

            lines.Add(JsonDocument.Parse(line).RootElement.Clone());
        }

        return lines;
    }

    private sealed class SettableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private sealed class DateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly>
    {
        public override void SetValue(System.Data.IDbDataParameter parameter, DateOnly value) =>
            parameter.Value = value.ToDateTime(TimeOnly.MinValue);

        public override DateOnly Parse(object value) => DateOnly.FromDateTime((DateTime)value);
    }

    /// <summary>Wraps the real, MinIO-backed <see cref="IFileStorage"/> and fails only
    /// <see cref="CreateUploadAsync"/> - the one call <see cref="MessageArchiveWriter"/>/
    /// <see cref="MessageArchiveJob"/> makes before it ever writes a byte to storage. Every other
    /// method (used by the seeding helpers above, and by the happy-path test's own retrieval assertions)
    /// still goes straight through to the real implementation - this is a real Postgres and a real
    /// object storage protocol throughout, with exactly one call point made to fail, not a mock
    /// asserting call order.</summary>
    private sealed class ThrowingOnUploadFileStorage(IFileStorage inner) : IFileStorage
    {
        public bool UploadWasAttempted { get; private set; }

        public Task<PresignedUpload> CreateUploadAsync(ObjectKey key, UploadConstraints constraints, CancellationToken cancellationToken)
        {
            UploadWasAttempted = true;
            throw new FileStorageUnavailableException("Simulated object storage outage for archive upload.");
        }

        public Task<Uri> CreateDownloadUrlAsync(ObjectKey key, TimeSpan lifetime, CancellationToken cancellationToken) =>
            inner.CreateDownloadUrlAsync(key, lifetime, cancellationToken);

        public Task<ObjectMetadata?> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner.GetMetadataAsync(key, cancellationToken);

        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) => inner.DeleteAsync(key, cancellationToken);
    }
}
