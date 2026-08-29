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
        var (conversationId, objectKey, thumbnailKey) = await SeedConversationWithAttachmentAsync(siteId);

        // `18-04`: a note and a tag - both personal data about this tenant's visitor, so both must be
        // gone once the whole account is erased, tag *vocabulary* included this time (unlike the
        // narrower single-conversation case, there is no other conversation left for it to survive
        // for).
        var tagId = await SeedTagAsync(siteId, "priority");
        await using (var db = fixture.CreateDbContext())
        {
            await new TagRepository(db).AddToConversationAsync(conversationId, tagId, CancellationToken.None);
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

        // The real HTTP-facing write: permission-checked, one flag set, no deletion here.
        var erasureRequests = new ErasureRequestRepository(fixture.DataSource);
        await using (var permissionDb = fixture.CreateDbContext())
        {
            var requestHandler = new RequestSiteErasureHandler(
                erasureRequests, new PermissionChecker(permissionDb), clock);
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

    private ConversationErasureJob CreateConversationJob(IClock clock) =>
        new(fixture.DataSource, fixture.FileStorage, clock,
            Options.Create(new ConversationErasureJobOptions()), NullLogger<ConversationErasureJob>.Instance);

    private SiteErasureJob CreateSiteJob(IClock clock, IDemoIdentityProvisioner identities, CacheInvalidationPublisher cacheInvalidation) =>
        new(fixture.DataSource, identities, cacheInvalidation, new UuidV7Generator(), clock,
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
    private async Task<(ConversationId ConversationId, string ObjectKey, string ThumbnailKey)> SeedConversationWithAttachmentAsync(SiteId siteId)
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

        return (conversation.Id, objectKey, thumbnailKey);
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
