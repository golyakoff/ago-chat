using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.CreateAttachment;
using Ago.Chat.Application.UseCases.GetAttachmentDownloadUrl;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetAttachmentDownloadUrl;

public class GetAttachmentDownloadUrlHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        GetAttachmentDownloadUrlHandler Handler, FakeFileStorage FileStorage, Attachment Attachment, FakePermissionChecker Permissions);

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
        var handler = new GetAttachmentDownloadUrlHandler(
            attachments, conversations, fileStorage, permissions, new FakeCache(), new AttachmentOptions(), new FakeClock(Now));

        return new Fixture(handler, fileStorage, attachment, permissions);
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
}
