using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Caching;
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
/// `16-02`'s own proof-of-completeness Done-when, end to end against a real Postgres, a real MinIO and
/// a real Keycloak: a tenant is created with operators, conversations, messages, attachments and
/// thumbnails, its erasure is requested through the real HTTP-facing handler, the two Worker jobs are
/// driven directly (the same `internal SweepAsync` seam <see cref="DemoTenantExpiryJob"/>/
/// <c>AttachmentOrphanSweepJob</c> already expose, for the identical reason - proving this without
/// waiting for a `PeriodicTimer`), and then every store `personal-data.md` lists for a tenant is
/// checked for emptiness - table by table, not "the site row is gone", because a deletion test that
/// only checks what it remembers to check is exactly how erasure quietly becomes partial (`16-02`'s own
/// Scope says so in as many words).
/// </summary>
[Collection(ErasureCollection.Name)]
public class SiteErasureIntegrationTests(ErasureFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);

    private sealed class SettableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    [Fact]
    public async Task ErasingASite_RemovesEverythingAcrossPostgresMinioAndKeycloak()
    {
        var clock = new SettableClock(Now);

        var (siteId, publicKey) = await SeedSiteAsync("erasure-site-1");
        var (adminOperatorId, subjectId) = await SeedAdminOperatorAsync(siteId);
        var (conversationId, visitorId, objectKey, thumbnailKey) = await SeedConversationWithAttachmentAsync(siteId);

        // `23-08`, Done-when #5: "deleting a site still cascades them, unchanged" - the visitor's
        // contact detail reaches removal through the pre-existing FK chain (sites -> visitors ->
        // visitor_contact_details, both ON DELETE CASCADE - VisitorContactDetailConfiguration's own
        // remarks call this the backstop, not the primary mechanism), so no code in SiteErasureJob
        // needed to change for this Done-when; only the assertion below is new.
        await using (var db = fixture.CreateDbContext())
        {
            await new VisitorContactDetailRepository(db).SaveAsync(
                VisitorContactDetail.Record(
                    new VisitorContactDetailId(Guid.NewGuid()), visitorId, VisitorContactDetailKind.Phone,
                    "+7 000 000-00-04", adminOperatorId, Now),
                CancellationToken.None);
        }

        // `18-04`: a note and a tag - both personal data about this tenant's visitor, so both must be
        // gone once the whole account is erased, tag *vocabulary* included this time (unlike the
        // narrower single-conversation case, there is no other conversation left for it to survive
        // for).
        var tagId = await SeedTagAsync(siteId, "priority");
        await using (var db = fixture.CreateDbContext())
        {
            await new TagRepository(db).AddToConversationAsync(conversationId, tagId, TagSource.Operator, CancellationToken.None);
            await new NoteRepository(db).SaveAsync(
                ConversationNote.Write(new ConversationNoteId(Guid.NewGuid()), conversationId, adminOperatorId, "erasure test note", Now),
                CancellationToken.None);
        }

        Assert.True(await fixture.UserExistsAsync(subjectId));
        Assert.NotNull(await fixture.FileStorage.GetMetadataAsync(new ObjectKey(objectKey), CancellationToken.None));
        Assert.NotNull(await fixture.FileStorage.GetMetadataAsync(new ObjectKey(thumbnailKey), CancellationToken.None));
        Assert.Equal(2, await CountAsync("select count(*) from messages where conversation_id = @conversationId", conversationId.Value));
        Assert.Equal(1, await CountAsync("select count(*) from attachments where conversation_id = @conversationId", conversationId.Value));
        Assert.Equal(1, await CountAsync("select count(*) from conversation_notes where conversation_id = @conversationId", conversationId.Value));
        Assert.Equal(1, await CountAsync("select count(*) from conversation_tags where conversation_id = @conversationId", conversationId.Value));
        Assert.Equal(1, await CountAsync("select count(*) from tags where id = @siteId", tagId.Value));
        Assert.Equal(1, await CountAsync("select count(*) from visitor_contact_details where visitor_id = @siteId", visitorId.Value));

        // The real HTTP-facing write: permission-checked, one flag set, no deletion here.
        var erasureRequests = new ErasureRequestRepository(fixture.DataSource);
        await using (var permissionDb = fixture.CreateDbContext())
        {
            var requestHandler = new RequestSiteErasureHandler(
                erasureRequests, new PermissionChecker(permissionDb), new UuidV7Generator(), clock);
            var requested = await requestHandler.HandleAsync(
                new RequestSiteErasure(siteId, adminOperatorId), CancellationToken.None);
            Assert.True(requested.IsSuccess, requested.IsFailure ? requested.Error!.Value.ToString() : null);
        }

        Assert.Equal(1, await CountAsync("select count(*) from sites where id = @siteId and erasure_requested_at is not null", siteId.Value));

        // Nothing deleted yet - "deletion is a job, not a request handler" (16-02's own Scope).
        Assert.Equal(1, await CountAsync("select count(*) from sites where id = @siteId", siteId.Value));
        Assert.Equal(1, await CountAsync("select count(*) from conversations where id = @conversationId", conversationId.Value));

        var recordingPublisher = new RecordingEventPublisher();
        var cacheInvalidation = new CacheInvalidationPublisher(recordingPublisher, clock);
        var provisioner = CreateProvisioner();
        var conversationJob = CreateConversationJob(clock);
        var siteJob = CreateSiteJob(clock, provisioner, cacheInvalidation);

        // Bounded convergence loop: ConversationErasureJob must drain the site's conversations before
        // SiteErasureJob's own gate (HasAnyConversationAsync) will let the site row go - a handful of
        // paired sweeps is enough to converge for this small a seed, matching how the two jobs would
        // actually interleave in production over a few real PeriodicTimer ticks.
        for (var i = 0; i < 5; i++)
        {
            await conversationJob.SweepAsync(CancellationToken.None);
            await siteJob.SweepAsync(CancellationToken.None);
        }

        // Postgres: every table `personal-data.md` lists for a tenant.
        Assert.Equal(0, await CountAsync("select count(*) from sites where id = @siteId", siteId.Value));
        Assert.Equal(0, await CountAsync("select count(*) from conversations where site_id = @siteId", siteId.Value));
        Assert.Equal(0, await CountAsync(
            "select count(*) from messages m join conversations c on c.id = m.conversation_id where c.site_id = @siteId",
            siteId.Value));
        Assert.Equal(0, await CountAsync("select count(*) from attachments where site_id = @siteId", siteId.Value));
        Assert.Equal(0, await CountAsync("select count(*) from operators where site_id = @siteId", siteId.Value));
        Assert.Equal(0, await CountAsync("select count(*) from roles where site_id = @siteId", siteId.Value));
        Assert.Equal(0, await CountAsync("select count(*) from visitors where site_id = @siteId", siteId.Value));
        // `23-08`, Done-when #5: cascaded via visitors, unchanged from before this item.
        Assert.Equal(0, await CountAsync("select count(*) from visitor_contact_details where visitor_id = @siteId", visitorId.Value));
        // `18-04`: the note and the tag *association* are drained per-conversation by
        // ConversationErasureJob (the same table this test already proves for messages/attachments
        // above), and the tag *definition* itself is gone too - the only other conversation that could
        // have kept it alive never existed in this test, so SiteErasureQuery.DeleteSiteAsync's own
        // cascade is what removes it (SiteErasureQuery's own remarks on why `tags` is the one table in
        // its cascade list that still had rows at that point).
        Assert.Equal(0, await CountAsync("select count(*) from conversation_notes where conversation_id = @conversationId", conversationId.Value));
        Assert.Equal(0, await CountAsync("select count(*) from conversation_tags where conversation_id = @conversationId", conversationId.Value));
        Assert.Equal(0, await CountAsync("select count(*) from tags where site_id = @siteId", siteId.Value));

        // MinIO: both the object and 5-04's thumbnail beside it.
        Assert.Null(await fixture.FileStorage.GetMetadataAsync(new ObjectKey(objectKey), CancellationToken.None));
        Assert.Null(await fixture.FileStorage.GetMetadataAsync(new ObjectKey(thumbnailKey), CancellationToken.None));

        // Keycloak: the operator's identity, queried against the real Admin API.
        Assert.False(await fixture.UserExistsAsync(subjectId));

        // Cache invalidation: both SiteCacheKeys, the same 14-04 two-key shape
        // SiteCacheInvalidationConsumer already broadcasts for an ordinary settings write.
        var publishedKeys = recordingPublisher.Published.Select(e => e.PartitionKey).ToList();
        Assert.Contains(SiteCacheKeys.ForPublicKey(publicKey).Value, publishedKeys);
        Assert.Contains(SiteCacheKeys.ForSiteId(siteId).Value, publishedKeys);
        Assert.All(recordingPublisher.Published, e => Assert.Equal(CacheTopics.Invalidated, e.Type));
    }

    /// <summary>
    /// `24-09`'s own second Done-when for this job: "the same question for `SiteErasureJob` - a
    /// `message_archives` row cascades with the site, and `personal-data.md` records that the `.zip` is
    /// then orphaned in storage." One site with exactly one conversation, archived for real before the
    /// site is erased - by the time this method's own convergence loop finishes,
    /// <see cref="ConversationErasureJob"/> has already scrubbed this conversation's lines out of the
    /// archive object (the same mechanism <c>ConversationErasureIntegrationTests</c> proves directly),
    /// so what is left standing is an empty shell this job must now delete itself, since a foreign key
    /// cannot reach into object storage. Proves both halves: the manifest row is gone (the ordinary
    /// cascade) <i>and</i> the object it named is gone too (the new, explicit step).
    /// </summary>
    [Fact]
    public async Task ErasingASite_DeletesItsArchiveObjectToo_NotOnlyTheManifestRow()
    {
        var referenceNow = new DateTimeOffset(2010, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var archivedAt = new DateTimeOffset(2010, 1, 15, 12, 0, 0, TimeSpan.Zero);
        const int retentionHorizonMonths = 3;

        var (siteId, _) = await SeedSiteAsync("archive-site-erasure-site");
        var (adminOperatorId, subjectId) = await SeedAdminOperatorAsync(siteId);
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, referenceNow);
        await using (var db = fixture.CreateDbContext())
        {
            db.Visitors.Add(new Visitor(visitorId, siteId, referenceNow));
            await db.SaveChangesAsync();
            db.Conversations.Add(conversation);
            await db.SaveChangesAsync();
        }

        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                insert into messages (id, conversation_id, sequence, author_kind, author_id, body, created_at, retention_class, site_id)
                values (@id, @conversationId, 1, 'Visitor', @authorId, @body, @createdAt, @retentionClass, @siteId)
                """,
                new
                {
                    id = Guid.NewGuid(),
                    conversationId = conversation.Id.Value,
                    authorId = Guid.NewGuid(),
                    body = "archived before the site is erased",
                    createdAt = archivedAt,
                    retentionClass = RetentionClass.Free.Value,
                    siteId = siteId.Value,
                });
        }

        var archiveJob = CreateArchiveJob(new SettableClock(referenceNow), retentionHorizonMonths);
        Assert.Equal(1, await archiveJob.ArchiveAsync(CancellationToken.None));

        var archiveRepository = new MessageArchiveRepository(fixture.DataSource);
        var archivedPeriod = Assert.Single(await archiveRepository.ListForSiteAsync(siteId, CancellationToken.None));
        Assert.NotNull(await fixture.FileStorage.GetMetadataAsync(new ObjectKey(archivedPeriod.ObjectKey), CancellationToken.None));

        var clock = new SettableClock(referenceNow);
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

        Assert.Equal(0, await CountAsync("select count(*) from sites where id = @siteId", siteId.Value));
        // The manifest row: gone via the ordinary sites cascade.
        Assert.Equal(0, await CountAsync("select count(*) from message_archives where site_id = @siteId", siteId.Value));
        // `24-09`'s own new step: the object the row named is gone too, not merely orphaned.
        Assert.Null(await fixture.FileStorage.GetMetadataAsync(new ObjectKey(archivedPeriod.ObjectKey), CancellationToken.None));

        Assert.False(await fixture.UserExistsAsync(subjectId));
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

    /// <summary>The real adapter (`KeycloakDemoIdentityProvisioner`), not a double - `IDemoIdentityProvisioner.DeleteAsync`
    /// is what <see cref="SiteErasureJob"/> actually calls in production, and this item's brief is
    /// explicit that reuse-as-is (rather than a rename) is the call this item makes for that port.</summary>
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

    private async Task<(SiteId SiteId, string PublicKey)> SeedSiteAsync(string name)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var publicKey = $"site_{siteId.Value:N}";
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, publicKey, ["https://shop.example"], name));
        await db.SaveChangesAsync();
        return (siteId, publicKey);
    }

    private async Task<(OperatorId OperatorId, string SubjectId)> SeedAdminOperatorAsync(SiteId siteId)
    {
        var subjectId = await fixture.CreateOperatorUserAsync($"erasure-admin-{Guid.NewGuid():N}"[..24]);
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

    /// <summary>A visitor, a conversation, a message and a real attachment (object + thumbnail,
    /// actually uploaded to MinIO) - so the erasure assertions are about a tenant that genuinely held
    /// something, the same "a deletion proven only against empty tables proves that empty tables stay
    /// empty" reasoning <c>DemoTenantLifecycleTests.SeedConversationAsync</c>'s own remarks give.</summary>
    private async Task<(ConversationId ConversationId, VisitorId VisitorId, string ObjectKey, string ThumbnailKey)> SeedConversationWithAttachmentAsync(SiteId siteId)
    {
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, Now);
        conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("hello"), Now);
        conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("is anyone there"), Now);

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
        return await connection.ExecuteScalarAsync<int>(sql, new { siteId = id, conversationId = id });
    }
}
