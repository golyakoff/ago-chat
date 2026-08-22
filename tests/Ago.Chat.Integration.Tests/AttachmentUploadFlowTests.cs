using Ago.Chat.Application.UseCases.ConfirmAttachment;
using Ago.Chat.Application.UseCases.CreateAttachment;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `5-03`'s Done-when: the whole upload flow against real Postgres and real MinIO, no mocking
/// (testing.md) - presign via <see cref="CreateAttachmentHandler"/>, a bare <see cref="HttpClient"/>
/// PUT exactly the way a browser would (same discipline as ago-platform's own `S3FileStorageTests`),
/// then <see cref="ConfirmAttachmentHandler"/>'s real HEAD-verify against the object that PUT actually
/// created.
/// </summary>
[Collection(AttachmentCollection.Name)]
public sealed class AttachmentUploadFlowTests(AttachmentFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly HttpClient Http = new();

    [Fact]
    public async Task FullFlow_PresignPutConfirm_EndsReadyWithMatchingMetadata()
    {
        var (visitorId, conversationId) = await SeedWaitingConversationAsync();
        var body = "hello from 5-03"u8.ToArray();

        await using var createDb = fixture.CreateDbContext();
        var created = await CreateHandler(createDb).HandleAsVisitorAsync(
            new CreateAttachmentAsVisitor(conversationId, visitorId, "image/png", body.Length), CancellationToken.None);
        Assert.True(created.IsSuccess);

        using var putContent = new ByteArrayContent(body);
        putContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        var putResponse = await Http.PutAsync(created.Value.UploadUrl, putContent);
        putResponse.EnsureSuccessStatusCode();

        await using var confirmDb = fixture.CreateDbContext();
        var confirmed = await CreateConfirmHandler(confirmDb).HandleAsVisitorAsync(
            new ConfirmAttachmentAsVisitor(new AttachmentId(created.Value.AttachmentId), visitorId), CancellationToken.None);

        Assert.True(confirmed.IsSuccess);
        await using var verifyDb = fixture.CreateDbContext();
        var attachment = await verifyDb.Attachments.SingleAsync(a => a.Id == new AttachmentId(created.Value.AttachmentId));
        Assert.Equal(AttachmentState.Ready, attachment.State);
        Assert.Equal(body.Length, attachment.SizeBytes);
        Assert.Equal("image/png", attachment.ContentType);
    }

    [Fact]
    public async Task Confirm_WithoutEverPuttingTheObject_FailsAndStaysPending()
    {
        var (visitorId, conversationId) = await SeedWaitingConversationAsync();

        await using var createDb = fixture.CreateDbContext();
        var created = await CreateHandler(createDb).HandleAsVisitorAsync(
            new CreateAttachmentAsVisitor(conversationId, visitorId, "image/png", 16), CancellationToken.None);
        Assert.True(created.IsSuccess);

        await using var confirmDb = fixture.CreateDbContext();
        var confirmed = await CreateConfirmHandler(confirmDb).HandleAsVisitorAsync(
            new ConfirmAttachmentAsVisitor(new AttachmentId(created.Value.AttachmentId), visitorId), CancellationToken.None);

        Assert.True(confirmed.IsFailure);
        Assert.Equal("Attachment.VerificationFailed", confirmed.Error!.Value.Code);
        await using var verifyDb = fixture.CreateDbContext();
        var attachment = await verifyDb.Attachments.SingleAsync(a => a.Id == new AttachmentId(created.Value.AttachmentId));
        Assert.Equal(AttachmentState.Pending, attachment.State);
    }

    [Fact]
    public async Task Confirm_WhenTheUploadedBytesDontMatchTheDeclaredSize_FailsAndStaysPending()
    {
        var (visitorId, conversationId) = await SeedWaitingConversationAsync();

        await using var createDb = fixture.CreateDbContext();
        var created = await CreateHandler(createDb).HandleAsVisitorAsync(
            new CreateAttachmentAsVisitor(conversationId, visitorId, "image/png", 5), CancellationToken.None);
        Assert.True(created.IsSuccess);

        // Declared 5 bytes at presign time, actually upload a different amount - HEAD-verify must
        // catch this even though the presigned PUT itself succeeded.
        using var putContent = new ByteArrayContent("a much longer body than declared"u8.ToArray());
        putContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        await Http.PutAsync(created.Value.UploadUrl, putContent);

        await using var confirmDb = fixture.CreateDbContext();
        var confirmed = await CreateConfirmHandler(confirmDb).HandleAsVisitorAsync(
            new ConfirmAttachmentAsVisitor(new AttachmentId(created.Value.AttachmentId), visitorId), CancellationToken.None);

        Assert.True(confirmed.IsFailure);
        Assert.Equal("Attachment.VerificationFailed", confirmed.Error!.Value.Code);
    }

    private CreateAttachmentHandler CreateHandler(AgoChatDbContext db) => new(
        new ConversationRepository(db),
        new AttachmentRepository(db),
        fixture.FileStorage,
        new FakeRateLimiter(),
        new PermissionChecker(db),
        new AttachmentOptions(),
        new AttachmentRateLimitOptions(),
        new UuidV7Generator(),
        new SystemClock());

    private ConfirmAttachmentHandler CreateConfirmHandler(AgoChatDbContext db) => new(
        new AttachmentRepository(db),
        new ConversationRepository(db),
        fixture.FileStorage,
        new PermissionChecker(db),
        new EfOutboxWriter<AgoChatDbContext>(db),
        new UuidV7Generator(),
        new SystemClock());

    private async Task<(VisitorId VisitorId, ConversationId ConversationId)> SeedWaitingConversationAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(visitorId, siteId, Now));
        db.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, Now));
        await db.SaveChangesAsync();

        return (visitorId, conversationId);
    }
}
