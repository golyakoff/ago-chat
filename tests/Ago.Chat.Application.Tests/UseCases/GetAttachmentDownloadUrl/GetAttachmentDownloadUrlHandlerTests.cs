using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.CreateAttachment;
using Ago.Chat.Application.UseCases.GetAttachmentDownloadUrl;
using Ago.Chat.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ago.Chat.Application.Tests.UseCases.GetAttachmentDownloadUrl;

public class GetAttachmentDownloadUrlHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        GetAttachmentDownloadUrlHandler Handler,
        FakeFileStorage FileStorage,
        Attachment Attachment,
        FakePermissionChecker Permissions,
        FakeAttachmentEgressMeter EgressMeter,
        FakeAttachmentRepository Attachments);

    private static Fixture CreateFixture(AttachmentState state = AttachmentState.Ready, bool assignOperator = true)
    {
        var conversations = new FakeConversationRepository();
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        if (assignOperator)
        {
            conversation.AssignTo(OperatorId, Now);
        }

        conversations.Seed(conversation);

        var attachment = Attachment.CreatePending(
            new AttachmentId(Guid.NewGuid()), SiteId, conversation.Id, "site/x/conv/y/z.png", "image/png", 42, Now);
        if (state != AttachmentState.Pending)
        {
            attachment.ConfirmReady(42, "image/png", Now);
        }

        if (state == AttachmentState.Deleted)
        {
            attachment.MarkDeleted();
        }

        var attachments = new FakeAttachmentRepository();
        attachments.Seed(attachment);

        var fileStorage = new FakeFileStorage();
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationRead);
        var egressMeter = new FakeAttachmentEgressMeter();
        var handler = new GetAttachmentDownloadUrlHandler(
            attachments,
            conversations,
            fileStorage,
            permissions,
            new FakeCache(),
            egressMeter,
            new AttachmentOptions(),
            new FakeClock(Now),
            NullLogger<GetAttachmentDownloadUrlHandler>.Instance);

        return new Fixture(handler, fileStorage, attachment, permissions, egressMeter, attachments);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_WhenAParticipantAndReady_ReturnsAUrl()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new GetAttachmentDownloadUrlAsVisitor(fixture.Attachment.Id, VisitorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Contains(fixture.Attachment.ObjectKey, result.Value.Url.ToString());
    }

    [Fact]
    public async Task HandleAsVisitorAsync_WhenNotAParticipant_ReturnsForbidden()
    {
        var fixture = CreateFixture();
        var someoneElse = new VisitorId(Guid.NewGuid());

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new GetAttachmentDownloadUrlAsVisitor(fixture.Attachment.Id, someoneElse), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsOperatorAsync_WhenNotAssignedToTheConversation_ReturnsForbidden()
    {
        var fixture = CreateFixture(assignOperator: false);

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new GetAttachmentDownloadUrlAsOperator(fixture.Attachment.Id, OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_WhenTheAttachmentIsStillPending_ReturnsNotReady()
    {
        var fixture = CreateFixture(state: AttachmentState.Pending);

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new GetAttachmentDownloadUrlAsVisitor(fixture.Attachment.Id, VisitorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Attachment.NotReady", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_WhenNoThumbnailWasEverGenerated_ReturnsANullThumbnailUrl()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new GetAttachmentDownloadUrlAsVisitor(fixture.Attachment.Id, VisitorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.ThumbnailUrl);
        Assert.Equal("image/png", result.Value.ContentType);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_WhenAThumbnailExists_ReturnsAPresignedThumbnailUrl()
    {
        var fixture = CreateFixture();
        fixture.Attachment.SetThumbnail("site/x/conv/y/z-thumb.webp");

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new GetAttachmentDownloadUrlAsVisitor(fixture.Attachment.Id, VisitorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value.ThumbnailUrl);
        Assert.Contains("z-thumb.webp", result.Value.ThumbnailUrl!.ToString());
    }

    [Fact]
    public async Task HandleAsVisitorAsync_CalledTwice_OnlyPresignsOnce_TheSecondCallIsServedFromCache()
    {
        var fixture = CreateFixture();

        var first = await fixture.Handler.HandleAsVisitorAsync(
            new GetAttachmentDownloadUrlAsVisitor(fixture.Attachment.Id, VisitorId), CancellationToken.None);
        var second = await fixture.Handler.HandleAsVisitorAsync(
            new GetAttachmentDownloadUrlAsVisitor(fixture.Attachment.Id, VisitorId), CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(first.Value.Url, second.Value.Url);
        Assert.Equal(1, fixture.FileStorage.CreateDownloadUrlCalls);
    }

    /// <summary>`23-80`: a deleted attachment must read as gone, permanently - distinct from
    /// `Attachment.NotReady` (transient, worth retrying). See ConversationErrors.AttachmentRemoved's
    /// own remarks for why the two codes must not collapse into one.</summary>
    [Fact]
    public async Task HandleAsVisitorAsync_WhenTheAttachmentWasDeleted_ReturnsRemoved_NotNotReady()
    {
        var fixture = CreateFixture(state: AttachmentState.Deleted);

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new GetAttachmentDownloadUrlAsVisitor(fixture.Attachment.Id, VisitorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Attachment.Removed", result.Error!.Value.Code);
    }

    /// <summary>`23-82`/`23-80`: the one place a download is counted - "at the point a presigned GET
    /// is issued." A fresh presign (a cache miss) must bump the attachment's own counter and the
    /// site's monthly egress aggregate together.</summary>
    [Fact]
    public async Task HandleAsVisitorAsync_OnAFreshPresign_RecordsTheDownloadAndTheSiteEgress()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new GetAttachmentDownloadUrlAsVisitor(fixture.Attachment.Id, VisitorId), CancellationToken.None);

        Assert.True(result.IsSuccess);

        var saved = await fixture.Attachments.GetByIdAsync(fixture.Attachment.Id, CancellationToken.None);
        Assert.Equal(1, saved!.DownloadCount);
        Assert.Equal(Now, saved.LastDownloadedAt);

        var recorded = Assert.Single(fixture.EgressMeter.Records);
        Assert.Equal(SiteId, recorded.SiteId);
        Assert.Equal(new DateOnly(Now.Year, Now.Month, 1), recorded.PeriodMonth);
        Assert.Equal(fixture.Attachment.SizeBytes, recorded.Bytes);
    }

    /// <summary>The companion to the fresh-presign test above - a cache hit must not double-count,
    /// because `RecordDownloadAsync` only ever runs inside the cache factory.</summary>
    [Fact]
    public async Task HandleAsVisitorAsync_CalledTwiceWithinTheCacheWindow_RecordsExactlyOneDownload()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsVisitorAsync(
            new GetAttachmentDownloadUrlAsVisitor(fixture.Attachment.Id, VisitorId), CancellationToken.None);
        await fixture.Handler.HandleAsVisitorAsync(
            new GetAttachmentDownloadUrlAsVisitor(fixture.Attachment.Id, VisitorId), CancellationToken.None);

        var saved = await fixture.Attachments.GetByIdAsync(fixture.Attachment.Id, CancellationToken.None);
        Assert.Equal(1, saved!.DownloadCount);
        Assert.Single(fixture.EgressMeter.Records);
    }
}
