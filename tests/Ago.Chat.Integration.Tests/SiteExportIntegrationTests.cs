using System.IO.Compression;
using System.Text.Json;
using Ago.Chat.Application.UseCases.GetSiteExportStatus;
using Ago.Chat.Application.UseCases.RequestSiteExport;
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
/// `16-03`'s own Done-when, end to end against a real Postgres and a real MinIO (the same
/// <see cref="AttachmentFixture"/> combination <c>AttachmentThumbnailEndToEndTests</c> already uses -
/// no Keycloak needed here, unlike <see cref="ErasureFixture"/>, since export never touches identity):
/// a tenant is seeded with an operator, a visitor, a conversation with two messages, a channel
/// identity, and a real attachment uploaded to MinIO; an export is triggered through the real
/// HTTP-facing handler, <see cref="SiteExportJob"/> is driven directly (the same
/// <c>internal SweepAsync</c> seam every other job in this codebase exposes for exactly this), and the
/// resulting archive is downloaded from its own presigned URL and its *actual contents* are checked
/// against `personal-data.md`'s stores - not just that a file exists (`16-03`'s own Done-when says so
/// in as many words).
/// </summary>
[Collection(AttachmentCollection.Name)]
public class SiteExportIntegrationTests(AttachmentFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);
    private static readonly HttpClient Http = new();

    private sealed class SettableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    [Fact]
    public async Task ExportingASite_ProducesAnArchive_WhoseContentsMatchEveryPersonalDataStore()
    {
        var clock = new SettableClock(Now);

        var (siteId, siteName, allowedOrigin) = await SeedSiteAsync("export-site-1");
        var operatorId = await SeedOperatorAsync(siteId, Permission.SiteExport);
        var (visitorId, conversationId, messageIds) = await SeedConversationAsync(siteId);
        var channelAddress = await SeedChannelIdentityAsync(siteId, visitorId);
        var (attachmentBytes, attachmentObjectKey) = await SeedAttachmentAsync(siteId, conversationId, messageIds[0]);

        // `18-04`: a note and a tag - both in scope for export, SiteExportArchiveWriter's own remarks.
        var tagId = await SeedTagAsync(siteId, "vip");
        await using (var db = fixture.CreateDbContext())
        {
            await new TagRepository(db).AddToConversationAsync(conversationId, tagId, TagSource.Operator, CancellationToken.None);
            await new NoteRepository(db).SaveAsync(
                ConversationNote.Write(new ConversationNoteId(Guid.NewGuid()), conversationId, operatorId, "export test note", Now),
                CancellationToken.None);
        }

        // The real HTTP-facing write: permission-checked, rate-limited, one row inserted, no
        // packaging here.
        var exportRequests = new ExportRequestRepository(fixture.DataSource);
        Guid exportId;
        await using (var permissionDb = fixture.CreateDbContext())
        {
            var requestHandler = new RequestSiteExportHandler(
                exportRequests, new FakeRateLimiter(), new PermissionChecker(permissionDb),
                new SiteExportRateLimitOptions(), new UuidV7Generator(), clock);
            var requested = await requestHandler.HandleAsync(
                new RequestSiteExport(siteId, operatorId), CancellationToken.None);
            Assert.True(requested.IsSuccess, requested.IsFailure ? requested.Error!.Value.ToString() : null);
            exportId = requested.Value;
        }

        Assert.Equal(1, await CountAsync(
            "select count(*) from export_requests where id = @id and status = 'Pending'", exportId));

        // Nothing built yet - "no packaging work in the handler" (RequestSiteExportHandler's own
        // remarks).
        var job = CreateJob(clock);
        var completed = await job.SweepAsync(CancellationToken.None);
        Assert.Equal(1, completed);

        // The completion poll: Ready, with a working download URL.
        Uri downloadUrl;
        await using (var permissionDb = fixture.CreateDbContext())
        {
            var statusHandler = new GetSiteExportStatusHandler(
                exportRequests, fixture.FileStorage, new PermissionChecker(permissionDb),
                new SiteExportOptions());
            var status = await statusHandler.HandleAsync(
                new GetSiteExportStatus(exportId, siteId, operatorId), CancellationToken.None);
            Assert.True(status.IsSuccess, status.IsFailure ? status.Error!.Value.ToString() : null);
            Assert.Equal(ExportStatus.Ready, status.Value.Status);
            Assert.NotNull(status.Value.DownloadUrl);
            downloadUrl = status.Value.DownloadUrl!;
        }

        using var archiveResponse = await Http.GetAsync(downloadUrl);
        archiveResponse.EnsureSuccessStatusCode();
        var archiveBytes = await archiveResponse.Content.ReadAsByteArrayAsync();

        using var archiveStream = new MemoryStream(archiveBytes);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);

        // manifest.json: format version + the store list this item includes.
        var manifest = await ReadJsonAsync(archive, "manifest.json");
        Assert.Equal(1, manifest.GetProperty("formatVersion").GetInt32());
        Assert.Equal(siteId.Value, manifest.GetProperty("siteId").GetGuid());
        var stores = manifest.GetProperty("stores").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(
            new[]
            {
                "site", "operators", "visitors", "channelIdentities", "conversations", "messages", "attachments",
                "notes", "tags", "conversationTags",
            },
            stores);

        // site.json
        var site = await ReadJsonAsync(archive, "site.json");
        Assert.Equal(siteId.Value, site.GetProperty("id").GetGuid());
        Assert.Equal(siteName, site.GetProperty("name").GetString());
        Assert.Contains(allowedOrigin, site.GetProperty("allowedOrigins").EnumerateArray().Select(e => e.GetString()));

        // operators.jsonl - one row, no name/email (personal-data.md's own "operators holds no name,
        // no email" - this asserts the export does not silently widen that).
        var operatorRows = await ReadJsonLinesAsync(archive, "operators.jsonl");
        var operatorRow = Assert.Single(operatorRows);
        Assert.Equal(operatorId.Value, operatorRow.GetProperty("id").GetGuid());
        Assert.False(operatorRow.TryGetProperty("name", out _));
        Assert.False(operatorRow.TryGetProperty("email", out _));

        // visitors.jsonl
        var visitorRows = await ReadJsonLinesAsync(archive, "visitors.jsonl");
        Assert.Equal(visitorId.Value, Assert.Single(visitorRows).GetProperty("id").GetGuid());

        // channel_identities.jsonl
        var channelRows = await ReadJsonLinesAsync(archive, "channel_identities.jsonl");
        var channelRow = Assert.Single(channelRows);
        Assert.Equal(visitorId.Value, channelRow.GetProperty("visitorId").GetGuid());
        Assert.Equal(channelAddress, channelRow.GetProperty("externalAddress").GetString());

        // conversations.jsonl
        var conversationRows = await ReadJsonLinesAsync(archive, "conversations.jsonl");
        Assert.Equal(conversationId.Value, Assert.Single(conversationRows).GetProperty("id").GetGuid());

        // messages.jsonl - both messages, in order, with their bodies.
        var messageRows = await ReadJsonLinesAsync(archive, "messages.jsonl");
        Assert.Equal(2, messageRows.Count);
        Assert.Equal("hello", messageRows[0].GetProperty("body").GetString());
        Assert.Equal("is anyone there", messageRows[1].GetProperty("body").GetString());
        Assert.Equal(conversationId.Value, messageRows[0].GetProperty("conversationId").GetGuid());

        // attachments.jsonl - a working presigned download URL, not just a row.
        var attachmentRows = await ReadJsonLinesAsync(archive, "attachments.jsonl");
        var attachmentRow = Assert.Single(attachmentRows);
        Assert.Equal("image/png", attachmentRow.GetProperty("contentType").GetString());
        var attachmentDownloadUrl = attachmentRow.GetProperty("downloadUrl").GetString();
        Assert.False(string.IsNullOrEmpty(attachmentDownloadUrl));
        using var attachmentResponse = await Http.GetAsync(attachmentDownloadUrl);
        attachmentResponse.EnsureSuccessStatusCode();
        Assert.Equal(attachmentBytes, await attachmentResponse.Content.ReadAsByteArrayAsync());

        // Sanity: the object key this test asserted against MinIO metadata for is the same one the
        // archive's own attachment row was built from.
        Assert.NotNull(await fixture.FileStorage.GetMetadataAsync(new ObjectKey(attachmentObjectKey), CancellationToken.None));

        // `18-04`: notes.jsonl, tags.jsonl, conversation_tags.jsonl - the note's own body, the tag's
        // own name, and the association linking them to this conversation.
        var noteRows = await ReadJsonLinesAsync(archive, "notes.jsonl");
        var noteRow = Assert.Single(noteRows);
        Assert.Equal(conversationId.Value, noteRow.GetProperty("conversationId").GetGuid());
        Assert.Equal(operatorId.Value, noteRow.GetProperty("authorId").GetGuid());
        Assert.Equal("export test note", noteRow.GetProperty("body").GetString());

        var tagRows = await ReadJsonLinesAsync(archive, "tags.jsonl");
        var tagRow = Assert.Single(tagRows);
        Assert.Equal(tagId.Value, tagRow.GetProperty("id").GetGuid());
        Assert.Equal("vip", tagRow.GetProperty("name").GetString());

        var conversationTagRows = await ReadJsonLinesAsync(archive, "conversation_tags.jsonl");
        var conversationTagRow = Assert.Single(conversationTagRows);
        Assert.Equal(conversationId.Value, conversationTagRow.GetProperty("conversationId").GetGuid());
        Assert.Equal(tagId.Value, conversationTagRow.GetProperty("tagId").GetGuid());
    }

    [Fact]
    public async Task ATenantCannotExportOrPollAnotherTenantsData()
    {
        var clock = new SettableClock(Now);

        var (siteAId, _, _) = await SeedSiteAsync("export-site-a");
        var (siteBId, _, _) = await SeedSiteAsync("export-site-b");
        var operatorAId = await SeedOperatorAsync(siteAId, Permission.SiteExport);
        var operatorBId = await SeedOperatorAsync(siteBId, Permission.SiteExport);

        var exportRequests = new ExportRequestRepository(fixture.DataSource);

        // Site A's own, genuine export request.
        Guid exportIdForSiteA;
        await using (var db = fixture.CreateDbContext())
        {
            var requestHandler = new RequestSiteExportHandler(
                exportRequests, new FakeRateLimiter(), new PermissionChecker(db),
                new SiteExportRateLimitOptions(), new UuidV7Generator(), clock);
            var requested = await requestHandler.HandleAsync(new RequestSiteExport(siteAId, operatorAId), CancellationToken.None);
            Assert.True(requested.IsSuccess);
            exportIdForSiteA = requested.Value;
        }

        // Site B's own operator, who holds SiteExport only on site B, must be refused when asked to
        // trigger an export for site A.
        await using (var db = fixture.CreateDbContext())
        {
            var requestHandler = new RequestSiteExportHandler(
                exportRequests, new FakeRateLimiter(), new PermissionChecker(db),
                new SiteExportRateLimitOptions(), new UuidV7Generator(), clock);
            var forbidden = await requestHandler.HandleAsync(new RequestSiteExport(siteAId, operatorBId), CancellationToken.None);
            Assert.True(forbidden.IsFailure);
            Assert.Equal("Conversation.Forbidden", forbidden.Error!.Value.Code);
        }

        // ...and refused when asked to poll site A's own already-created export.
        await using (var db = fixture.CreateDbContext())
        {
            var statusHandler = new GetSiteExportStatusHandler(
                exportRequests, fixture.FileStorage, new PermissionChecker(db), new SiteExportOptions());
            var forbidden = await statusHandler.HandleAsync(
                new GetSiteExportStatus(exportIdForSiteA, siteAId, operatorBId), CancellationToken.None);
            Assert.True(forbidden.IsFailure);
            Assert.Equal("Conversation.Forbidden", forbidden.Error!.Value.Code);
        }

        // A second, deeper guard: an operator who genuinely holds SiteExport on *both* sites, polling
        // with site B's id in the route but site A's real export id, must still be refused - not with
        // Forbidden (the permission check on (operatorA, siteB) legitimately passes), but with
        // Export.NotFound, the same "wrong site is indistinguishable from no such id" cross-tenant
        // guard IErasureRequestRepository's own remarks describe, proven here against the real
        // ExportRequestRepository's own SQL rather than only against a fake.
        await GrantOperatorRoleAsync(operatorAId, siteBId, Permission.SiteExport);

        await using (var db = fixture.CreateDbContext())
        {
            var statusHandler = new GetSiteExportStatusHandler(
                exportRequests, fixture.FileStorage, new PermissionChecker(db), new SiteExportOptions());
            var wrongSite = await statusHandler.HandleAsync(
                new GetSiteExportStatus(exportIdForSiteA, siteBId, operatorAId), CancellationToken.None);
            Assert.True(wrongSite.IsFailure);
            Assert.Equal("Export.NotFound", wrongSite.Error!.Value.Code);
        }
    }

    private async Task GrantOperatorRoleAsync(OperatorId operatorId, SiteId siteId, Permission permission)
    {
        await using var db = fixture.CreateDbContext();
        var roleId = Guid.NewGuid();
        db.Roles.Add(new RoleRecord { Id = roleId, SiteId = siteId, Name = $"Grant-{roleId:N}", Permissions = [permission.Value] });
        db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
        await db.SaveChangesAsync();
    }

    private SiteExportJob CreateJob(IClock clock)
    {
        var options = new SiteExportJobOptions { AttachmentUrlLifetime = TimeSpan.FromMinutes(30) };
        var archiveWriter = new SiteExportArchiveWriter(fixture.FileStorage, options);
        return new SiteExportJob(
            fixture.DataSource, fixture.FileStorage, archiveWriter, clock,
            Options.Create(options), NullLogger<SiteExportJob>.Instance);
    }

    private async Task<(SiteId SiteId, string Name, string AllowedOrigin)> SeedSiteAsync(string name)
    {
        var siteId = new SiteId(Guid.NewGuid());
        const string origin = "https://shop.example";
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", [origin], name));
        await db.SaveChangesAsync();
        return (siteId, name, origin);
    }

    private async Task<OperatorId> SeedOperatorAsync(SiteId siteId, Permission permission)
    {
        var operatorId = new OperatorId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Operators.Add(new Operator(
            operatorId, siteId, OperatorStatus.Offline, capacity: 5, externalSubjectId: $"subject-{operatorId.Value:N}"));
        var roleId = Guid.NewGuid();
        db.Roles.Add(new RoleRecord { Id = roleId, SiteId = siteId, Name = "Admin", Permissions = [permission.Value] });
        db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
        await db.SaveChangesAsync();
        return operatorId;
    }

    private async Task<(VisitorId VisitorId, ConversationId ConversationId, IReadOnlyList<Guid> MessageIds)> SeedConversationAsync(SiteId siteId)
    {
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, Now);
        var firstMessageId = Guid.NewGuid();
        var secondMessageId = Guid.NewGuid();
        conversation.AddVisitorMessage(visitorId, new MessageId(firstMessageId), new MessageBody("hello"), Now);
        conversation.AddVisitorMessage(visitorId, new MessageId(secondMessageId), new MessageBody("is anyone there"), Now.AddSeconds(1));

        await using (var db = fixture.CreateDbContext())
        {
            db.Visitors.Add(new Visitor(visitorId, siteId, Now));
            await db.SaveChangesAsync();
            db.Conversations.Add(conversation);
            await db.SaveChangesAsync();
        }

        return (visitorId, conversation.Id, [firstMessageId, secondMessageId]);
    }

    private async Task<string> SeedChannelIdentityAsync(SiteId siteId, VisitorId visitorId)
    {
        const string address = "+70000000000";
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            insert into channel_identities (id, site_id, visitor_id, kind, external_address, first_seen_at, last_seen_at)
            values (@id, @siteId, @visitorId, 'Sms', @address, @now, @now)
            """,
            new { id = Guid.NewGuid(), siteId = siteId.Value, visitorId = visitorId.Value, address, now = Now });
        return address;
    }

    private async Task<(byte[] Bytes, string ObjectKey)> SeedAttachmentAsync(SiteId siteId, ConversationId conversationId, Guid messageId)
    {
        byte[] bytes = [1, 2, 3, 4, 5];
        var objectKey = $"site/{siteId.Value}/conv/{conversationId.Value}/{Guid.NewGuid():N}.png";
        await UploadTestObjectAsync(objectKey, bytes, "image/png");

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            insert into attachments (id, site_id, conversation_id, message_id, object_key, content_type, size_bytes, state, created_at)
            values (@id, @siteId, @conversationId, @messageId, @objectKey, 'image/png', @sizeBytes, 'Ready', @now)
            """,
            new
            {
                id = Guid.NewGuid(),
                siteId = siteId.Value,
                conversationId = conversationId.Value,
                messageId,
                objectKey,
                sizeBytes = (long)bytes.Length,
                now = Now,
            });

        return (bytes, objectKey);
    }

    /// <summary>Uploads real bytes to a real MinIO object through the same presign-then-PUT path a
    /// real client uses - the same shape <c>ErasureFixture.UploadTestObjectAsync</c> establishes,
    /// restated here since <see cref="AttachmentFixture"/> exposes only <see cref="IFileStorage"/>
    /// itself, not a test-upload helper.</summary>
    private async Task UploadTestObjectAsync(string key, byte[] bytes, string contentType)
    {
        var presigned = await fixture.FileStorage.CreateUploadAsync(
            new ObjectKey(key), new UploadConstraints(contentType, bytes.Length, TimeSpan.FromMinutes(5)), CancellationToken.None);
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        using var response = await Http.PutAsync(presigned.Url, content);
        response.EnsureSuccessStatusCode();
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

    private async Task<TagId> SeedTagAsync(SiteId siteId, string name)
    {
        var tag = Tag.Create(new TagId(Guid.NewGuid()), siteId, name, Now);
        await using var db = fixture.CreateDbContext();
        await new TagRepository(db).SaveAsync(tag, CancellationToken.None);
        return tag.Id;
    }

    private async Task<int> CountAsync(string sql, Guid id)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(sql, new { id });
    }
}
