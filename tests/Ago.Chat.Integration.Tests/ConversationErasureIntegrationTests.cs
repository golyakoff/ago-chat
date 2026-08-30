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

        // Both halves genuinely exist before erasure - the "narrower scope, same completeness" claim
        // is only interesting if there was something to distinguish in the first place.
        Assert.Equal(2, await CountAsync("select count(*) from messages where conversation_id = @id", toErase.ConversationId.Value));
        Assert.Equal(2, await CountAsync("select count(*) from messages where conversation_id = @id", toKeep.ConversationId.Value));
        Assert.Equal(1, await CountAsync("select count(*) from conversation_notes where conversation_id = @id", toErase.ConversationId.Value));
        Assert.Equal(1, await CountAsync("select count(*) from conversation_notes where conversation_id = @id", toKeep.ConversationId.Value));
        Assert.Equal(1, await CountAsync("select count(*) from conversation_tags where conversation_id = @id", toErase.ConversationId.Value));
        Assert.Equal(1, await CountAsync("select count(*) from conversation_tags where conversation_id = @id", toKeep.ConversationId.Value));
        Assert.NotNull(await fixture.FileStorage.GetMetadataAsync(new ObjectKey(toErase.ObjectKey), CancellationToken.None));
        Assert.NotNull(await fixture.FileStorage.GetMetadataAsync(new ObjectKey(toKeep.ObjectKey), CancellationToken.None));

        var erasureRequests = new ErasureRequestRepository(fixture.DataSource);
        await using (var permissionDb = fixture.CreateDbContext())
        {
            var requestHandler = new RequestConversationErasureHandler(
                erasureRequests, new PermissionChecker(permissionDb), clock);
            var requested = await requestHandler.HandleAsync(
                new RequestConversationErasure(toErase.ConversationId, adminOperatorId, siteId), CancellationToken.None);
            Assert.True(requested.IsSuccess, requested.IsFailure ? requested.Error!.Value.ToString() : null);
        }

        var conversationJob = new ConversationErasureJob(
            fixture.DataSource, fixture.FileStorage, clock,
            Options.Create(new ConversationErasureJobOptions()), NullLogger<ConversationErasureJob>.Instance);

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

        // The other conversation and the site itself: completely untouched.
        Assert.Equal(1, await CountAsync("select count(*) from conversations where id = @id", toKeep.ConversationId.Value));
        Assert.Equal(2, await CountAsync("select count(*) from messages where conversation_id = @id", toKeep.ConversationId.Value));
        Assert.Equal(1, await CountAsync("select count(*) from attachments where conversation_id = @id", toKeep.ConversationId.Value));
        Assert.Equal(1, await CountAsync("select count(*) from conversation_notes where conversation_id = @id", toKeep.ConversationId.Value));
        Assert.Equal(1, await CountAsync("select count(*) from conversation_tags where conversation_id = @id", toKeep.ConversationId.Value));
        Assert.NotNull(await fixture.FileStorage.GetMetadataAsync(new ObjectKey(toKeep.ObjectKey), CancellationToken.None));
        Assert.NotNull(await fixture.FileStorage.GetMetadataAsync(new ObjectKey(toKeep.ThumbnailKey), CancellationToken.None));
        Assert.Equal(1, await CountAsync("select count(*) from sites where id = @id", siteId.Value));

        // `18-04`: the tag *definition* itself survives - another conversation (toKeep) still carries
        // it, and a tag vocabulary entry is site-scoped, not conversation-scoped (TagConfiguration's
        // own remarks); only SiteErasureJob's whole-account cascade removes the definition row.
        Assert.Equal(1, await CountAsync("select count(*) from tags where id = @id", tagId.Value));
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

    private async Task<(ConversationId ConversationId, string ObjectKey, string ThumbnailKey)> SeedConversationWithAttachmentAsync(SiteId siteId)
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

        return (conversation.Id, objectKey, thumbnailKey);
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
