using System.IO.Compression;
using System.Text.Json;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.RequestConversationErasure;
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
/// `16-02`'s narrower Done-when: "a tenant can delete one conversation, with the same completeness."
/// One site, two conversations - erase one, and prove both halves of the claim together: the erased
/// conversation's messages, attachment and MinIO objects are gone, and the *other* conversation, its
/// own messages/attachment/objects, and the site itself are completely untouched. Real Postgres and
/// real MinIO, the same <see cref="ErasureFixture"/> <see cref="SiteErasureIntegrationTests"/> uses -
/// no Keycloak assertion needed here, since conversation erasure never touches an identity.
///
/// <para><b>`23-08`</b> adds the visitor's own contact details to the same completeness claim: the
/// erased conversation's visitor had one recorded (`docs/design/decisions.md` §4 - "a person's erasure
/// request takes the conversation and the contact, it is all their data"), and the kept conversation's
/// visitor had one too, to prove the removal is scoped to the erased visitor and not to every contact
/// detail on the site.</para>
/// </summary>
[Collection(ErasureCollection.Name)]
public class ConversationErasureIntegrationTests(ErasureFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);

    private sealed class SettableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    [Fact]
    public async Task ErasingOneConversation_RemovesOnlyItsOwnData_LeavingTheSiteAndItsOtherConversationIntact()
    {
        var clock = new SettableClock(Now);

        var siteId = await SeedSiteAsync("erasure-site-2");
        var (adminOperatorId, _) = await SeedOperatorAsync(siteId);
        var toErase = await SeedConversationWithAttachmentAsync(siteId);
        var toKeep = await SeedConversationWithAttachmentAsync(siteId);

        // `18-04`: one tag shared by both conversations (the vocabulary is per-site, not
        // per-conversation), plus one note each - both are personal data about a visitor
        // (ConversationNote's own remarks) and in scope for this same completeness claim.
        var tagId = await SeedTagAsync(siteId, "vip");
        await TagConversationAsync(toErase.ConversationId, tagId);
        await TagConversationAsync(toKeep.ConversationId, tagId);
        await SeedNoteAsync(toErase.ConversationId, adminOperatorId, "erase-me note");
        await SeedNoteAsync(toKeep.ConversationId, adminOperatorId, "keep-me note");

        // `23-08`: one contact detail per visitor - the operator's own annotation of a number a
        // visitor said out loud (`VisitorContactDetail`'s own remarks), fake and obviously so.
        await SeedContactDetailAsync(toErase.VisitorId, adminOperatorId, "+7 000 000-00-01");
        await SeedContactDetailAsync(toKeep.VisitorId, adminOperatorId, "+7 000 000-00-02");

        // Both halves genuinely exist before erasure - the "narrower scope, same completeness" claim
        // is only interesting if there was something to distinguish in the first place.
        Assert.Equal(2, await CountAsync("select count(*) from messages where conversation_id = @id", toErase.ConversationId.Value));
        Assert.Equal(2, await CountAsync("select count(*) from messages where conversation_id = @id", toKeep.ConversationId.Value));
        Assert.Equal(1, await CountAsync("select count(*) from conversation_notes where conversation_id = @id", toErase.ConversationId.Value));
        Assert.Equal(1, await CountAsync("select count(*) from conversation_notes where conversation_id = @id", toKeep.ConversationId.Value));
        Assert.Equal(1, await CountAsync("select count(*) from conversation_tags where conversation_id = @id", toErase.ConversationId.Value));
        Assert.Equal(1, await CountAsync("select count(*) from conversation_tags where conversation_id = @id", toKeep.ConversationId.Value));
        Assert.Equal(1, await CountAsync("select count(*) from visitor_contact_details where visitor_id = @id", toErase.VisitorId.Value));
        Assert.Equal(1, await CountAsync("select count(*) from visitor_contact_details where visitor_id = @id", toKeep.VisitorId.Value));
        Assert.NotNull(await fixture.FileStorage.GetMetadataAsync(new ObjectKey(toErase.ObjectKey), CancellationToken.None));
        Assert.NotNull(await fixture.FileStorage.GetMetadataAsync(new ObjectKey(toKeep.ObjectKey), CancellationToken.None));

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

        // The erased conversation: row, messages, attachment row and both MinIO objects gone.
        Assert.Equal(0, await CountAsync("select count(*) from conversations where id = @id", toErase.ConversationId.Value));
        Assert.Equal(0, await CountAsync("select count(*) from messages where conversation_id = @id", toErase.ConversationId.Value));
        Assert.Equal(0, await CountAsync("select count(*) from attachments where conversation_id = @id", toErase.ConversationId.Value));
        Assert.Null(await fixture.FileStorage.GetMetadataAsync(new ObjectKey(toErase.ObjectKey), CancellationToken.None));
        Assert.Null(await fixture.FileStorage.GetMetadataAsync(new ObjectKey(toErase.ThumbnailKey), CancellationToken.None));

        // `18-04`: the erased conversation's own note and tag association are gone too.
        Assert.Equal(0, await CountAsync("select count(*) from conversation_notes where conversation_id = @id", toErase.ConversationId.Value));
        Assert.Equal(0, await CountAsync("select count(*) from conversation_tags where conversation_id = @id", toErase.ConversationId.Value));

        // `23-08`, Done-when #2: erasing a conversation removes that visitor's contact details.
        Assert.Equal(0, await CountAsync("select count(*) from visitor_contact_details where visitor_id = @id", toErase.VisitorId.Value));

        // The other conversation and the site itself: completely untouched.
        Assert.Equal(1, await CountAsync("select count(*) from conversations where id = @id", toKeep.ConversationId.Value));
        Assert.Equal(2, await CountAsync("select count(*) from messages where conversation_id = @id", toKeep.ConversationId.Value));
        Assert.Equal(1, await CountAsync("select count(*) from attachments where conversation_id = @id", toKeep.ConversationId.Value));
        Assert.Equal(1, await CountAsync("select count(*) from conversation_notes where conversation_id = @id", toKeep.ConversationId.Value));
        Assert.Equal(1, await CountAsync("select count(*) from conversation_tags where conversation_id = @id", toKeep.ConversationId.Value));
        Assert.NotNull(await fixture.FileStorage.GetMetadataAsync(new ObjectKey(toKeep.ObjectKey), CancellationToken.None));
        Assert.NotNull(await fixture.FileStorage.GetMetadataAsync(new ObjectKey(toKeep.ThumbnailKey), CancellationToken.None));
        Assert.Equal(1, await CountAsync("select count(*) from sites where id = @id", siteId.Value));

        // `23-08`, Done-when #3: erasing one visitor's conversation does not remove another visitor's
        // contact details.
        Assert.Equal(1, await CountAsync("select count(*) from visitor_contact_details where visitor_id = @id", toKeep.VisitorId.Value));

        // `18-04`: the tag *definition* itself survives - another conversation (toKeep) still carries
        // it, and a tag vocabulary entry is site-scoped, not conversation-scoped (TagConfiguration's
        // own remarks); only SiteErasureJob's whole-account cascade removes the definition row.
        Assert.Equal(1, await CountAsync("select count(*) from tags where id = @id", tagId.Value));
    }

    /// <summary>
    /// `24-09`'s own Done-when: "an erasure that runs after a conversation's messages were archived
    /// leaves no copy of them in the archive." Two conversations on one site, each with one message
    /// dated far enough in the past to be past `13-06`'s own retention horizon, archived for real by a
    /// real <see cref="MessageArchiveJob"/> cycle (one object covers both, since they share a site,
    /// class and period, exactly `adr/0031`'s "one object per site per period" - the reason a whole-
    /// object delete would have destroyed the *kept* conversation's transcript too, `docs/adr/0108-*`'s
    /// own reasoning). Erasing one of the two must remove only its own lines from that shared object.
    ///
    /// <para>Deliberately does not also run `MessagePartitionPruneJob` to drop the live rows - this
    /// method's own removal is independent of whether the live `messages` row still exists
    /// (<see cref="ConversationArchiveEraser"/> reads only <c>message_archives</c> and the conversation
    /// id), so proving it here, against a still-live row, is exactly as strong a proof as proving it
    /// against a dropped one, for less test machinery.</para>
    /// </summary>
    [Fact]
    public async Task ErasingOneConversation_RemovesItsOwnLinesFromAnArchiveItSharesWithAnotherConversation()
    {
        var referenceNow = new DateTimeOffset(2010, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var archivedAt = new DateTimeOffset(2010, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var retentionClass = RetentionClass.Free;
        const int retentionHorizonMonths = 3;

        var siteId = await SeedSiteAsync("archive-erasure-site");
        var (adminOperatorId, _) = await SeedOperatorAsync(siteId);

        var (toEraseConversationId, toEraseVisitorId) = await SeedConversationAsync(siteId);
        var (toKeepConversationId, _) = await SeedConversationAsync(siteId);
        await SeedArchivedMessageAsync(siteId, toEraseConversationId, retentionClass, archivedAt, "erase me from the archive");
        await SeedArchivedMessageAsync(siteId, toKeepConversationId, retentionClass, archivedAt, "keep me in the archive");

        // Archives for real: one object, `messages.jsonl` carrying both conversations' lines.
        var archiveClock = new SettableClock(referenceNow);
        var archiveJob = CreateArchiveJob(archiveClock, retentionHorizonMonths);
        Assert.Equal(1, await archiveJob.ArchiveAsync(CancellationToken.None));

        var archiveRepository = new MessageArchiveRepository(fixture.DataSource);
        var archivedPeriod = Assert.Single(await archiveRepository.ListForSiteAsync(siteId, CancellationToken.None));
        var beforeLines = await DownloadMessageLinesAsync(archivedPeriod.ObjectKey);
        Assert.Equal(2, beforeLines.Count);

        // Now the erasure request, and the real job that drains it - the same shape the other test in
        // this file uses.
        var erasureClock = new SettableClock(referenceNow);
        var erasureRequests = new ErasureRequestRepository(fixture.DataSource);
        await using (var permissionDb = fixture.CreateDbContext())
        {
            var requestHandler = new RequestConversationErasureHandler(
                erasureRequests, new PermissionChecker(permissionDb), new UuidV7Generator(), erasureClock);
            var requested = await requestHandler.HandleAsync(
                new RequestConversationErasure(toEraseConversationId, adminOperatorId, siteId), CancellationToken.None);
            Assert.True(requested.IsSuccess, requested.IsFailure ? requested.Error!.Value.ToString() : null);
        }

        var conversationJob = CreateConversationJob(erasureClock);
        for (var i = 0; i < 3; i++)
        {
            await conversationJob.SweepAsync(CancellationToken.None);
        }

        // The conversation itself is gone, the ordinary Done-when this file already proves elsewhere.
        Assert.Equal(0, await CountAsync("select count(*) from conversations where id = @id", toEraseConversationId.Value));

        // `24-09`'s own claim: the archive object - the *same* object, same key, both conversations
        // shared - no longer carries the erased conversation's line, and still carries the other one's.
        var afterLines = await DownloadMessageLinesAsync(archivedPeriod.ObjectKey);
        var remaining = Assert.Single(afterLines);
        Assert.Equal(toKeepConversationId.Value, remaining.GetProperty("conversationId").GetGuid());
        Assert.DoesNotContain(afterLines, line => line.GetProperty("conversationId").GetGuid() == toEraseConversationId.Value);

        // The manifest row itself still stands - `24-09` rewrites the object, it does not remove the
        // manifest or the object wholesale, since the kept conversation's own line still lives there.
        Assert.Equal(1, await CountAsync(
            "select count(*) from message_archives where site_id = @id", siteId.Value));

        // The visitor whose conversation was erased: contact-detail/erasure completeness is proven by
        // the other test in this file; this one only needs the visitor id to exist for seeding.
        _ = toEraseVisitorId;
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

    private MessageArchiveJob CreateArchiveJob(IClock clock, int retentionHorizonMonths)
    {
        var archiveOptions = new MessageArchiveJobOptions();
        var writer = new MessageArchiveWriter(fixture.FileStorage, archiveOptions);
        return new MessageArchiveJob(
            fixture.DataSource, fixture.FileStorage, new MessageArchiveRepository(fixture.DataSource), writer, clock,
            new UuidV7Generator(),
            Options.Create(new MessagePartitionPruneJobOptions { RetentionHorizonMonths = retentionHorizonMonths }),
            Options.Create(archiveOptions), NullLogger<MessageArchiveJob>.Instance);
    }

    /// <summary>A conversation row with no messages of its own yet - <see cref="SeedArchivedMessageAsync"/>
    /// inserts the message directly, the same "bypass the aggregate to control `created_at`" convention
    /// <c>MessageRetentionArchiveEndToEndTests.SeedMessageAsync</c> already establishes.</summary>
    private async Task<(ConversationId ConversationId, VisitorId VisitorId)> SeedConversationAsync(SiteId siteId)
    {
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, DateTimeOffset.UtcNow);

        await using var db = fixture.CreateDbContext();
        db.Visitors.Add(new Visitor(visitorId, siteId, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();

        return (conversation.Id, visitorId);
    }

    private async Task SeedArchivedMessageAsync(
        SiteId siteId, ConversationId conversationId, RetentionClass retentionClass, DateTimeOffset createdAt, string body)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            insert into messages (id, conversation_id, sequence, author_kind, author_id, body, created_at, retention_class, site_id)
            values (@id, @conversationId, 1, 'Visitor', @authorId, @body, @createdAt, @retentionClass, @siteId)
            """,
            new
            {
                id = Guid.NewGuid(),
                conversationId = conversationId.Value,
                authorId = Guid.NewGuid(),
                body,
                createdAt,
                retentionClass = retentionClass.Value,
                siteId = siteId.Value,
            });
    }

    private static readonly HttpClient ArchiveHttp = new();

    private async Task<IReadOnlyList<JsonElement>> DownloadMessageLinesAsync(string objectKey)
    {
        var downloadUrl = await fixture.FileStorage.CreateDownloadUrlAsync(
            new ObjectKey(objectKey), TimeSpan.FromMinutes(5), CancellationToken.None);
        using var response = await ArchiveHttp.GetAsync(downloadUrl);
        response.EnsureSuccessStatusCode();
        using var stream = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var entry = archive.GetEntry("messages.jsonl") ?? throw new InvalidOperationException("Archive has no messages.jsonl entry.");
        await using var entryStream = entry.Open();
        using var reader = new StreamReader(entryStream);

        var lines = new List<JsonElement>();
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            if (line.Length > 0)
            {
                lines.Add(JsonDocument.Parse(line).RootElement.Clone());
            }
        }

        return lines;
    }

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
        var subjectId = await fixture.CreateOperatorUserAsync($"erasure-op-{Guid.NewGuid():N}"[..24]);
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

    /// <summary>`23-08`: through the real repository, exercising the same write path
    /// `RecordVisitorContactDetailHandler` uses, rather than a raw insert - the object under test is
    /// whether erasure reaches a row shaped the way production actually writes it.</summary>
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
