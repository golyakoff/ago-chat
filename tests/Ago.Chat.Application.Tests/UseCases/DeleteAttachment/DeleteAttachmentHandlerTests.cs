using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.DeleteAttachment;
using Ago.Chat.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ago.Chat.Application.Tests.UseCases.DeleteAttachment;

public class DeleteAttachmentHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(DeleteAttachmentHandler Handler, FakeAttachmentRepository Attachments, FakeFileStorage FileStorage, Attachment Attachment);

    private static Fixture CreateFixture(
        bool grantPermission = true, AttachmentState state = AttachmentState.Ready, SiteId? attachmentSiteId = null, bool withThumbnail = false)
    {
        var attachments = new FakeAttachmentRepository();
        var attachment = Attachment.CreatePending(
            new AttachmentId(Guid.NewGuid()), attachmentSiteId ?? SiteId, new ConversationId(Guid.NewGuid()),
            "site/x/conv/y/z.png", "image/png", 1024, Now);
        if (state == AttachmentState.Ready)
        {
            attachment.ConfirmReady(1024, "image/png", Now);
            if (withThumbnail)
            {
                attachment.SetThumbnail("site/x/conv/y/z_thumb.jpg");
            }
        }
        else if (state == AttachmentState.Deleted)
        {
            attachment.ConfirmReady(1024, "image/png", Now);
            if (withThumbnail)
            {
                attachment.SetThumbnail("site/x/conv/y/z_thumb.jpg");
            }

            attachment.MarkDeleted();
        }

        attachments.Seed(attachment);

        var fileStorage = new FakeFileStorage();
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.AttachmentDelete);
        }

        var handler = new DeleteAttachmentHandler(attachments, fileStorage, permissions, NullLogger<DeleteAttachmentHandler>.Instance);
        return new Fixture(handler, attachments, fileStorage, attachment);
    }

    [Fact]
    public async Task HandleAsOperatorAsync_WhenTheOperatorHoldsThePermission_DeletesTheRowAndTheStorageObject()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new DeleteAttachmentAsOperator(fixture.Attachment.Id, OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var reloaded = await fixture.Attachments.GetByIdAsync(fixture.Attachment.Id, CancellationToken.None);
        Assert.Equal(AttachmentState.Deleted, reloaded!.State);
        Assert.Equal(1, fixture.FileStorage.DeleteCalls);
    }

    [Fact]
    public async Task HandleAsOperatorAsync_WhenTheAttachmentHasAThumbnail_DeletesBothStorageObjects()
    {
        var fixture = CreateFixture(withThumbnail: true);

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new DeleteAttachmentAsOperator(fixture.Attachment.Id, OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        // The main object and the thumbnail - found live while manually verifying this item that the
        // first version of this handler only ever deleted the former, leaving a real orphaned
        // thumbnail behind in MinIO (this handler's own doc comment has the detail).
        Assert.Equal(2, fixture.FileStorage.DeleteCalls);
    }

    [Fact]
    public async Task HandleAsOperatorAsync_WithoutThePermission_ReturnsForbidden_WithoutDeleting()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new DeleteAttachmentAsOperator(fixture.Attachment.Id, OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Equal(0, fixture.FileStorage.DeleteCalls);
        var reloaded = await fixture.Attachments.GetByIdAsync(fixture.Attachment.Id, CancellationToken.None);
        Assert.Equal(AttachmentState.Ready, reloaded!.State);
    }

    [Fact]
    public async Task HandleAsOperatorAsync_WhenTheAttachmentBelongsToAnotherSite_ReturnsNotFound_WithoutDeleting()
    {
        var otherSite = new SiteId(Guid.NewGuid());
        var fixture = CreateFixture(attachmentSiteId: otherSite);

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new DeleteAttachmentAsOperator(fixture.Attachment.Id, OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Attachment.NotFound", result.Error!.Value.Code);
        Assert.Equal(0, fixture.FileStorage.DeleteCalls);
    }

    [Fact]
    public async Task HandleAsOperatorAsync_WhenAlreadyDeleted_IsIdempotent_AndDoesNotCallStorageAgain()
    {
        var fixture = CreateFixture(state: AttachmentState.Deleted);

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new DeleteAttachmentAsOperator(fixture.Attachment.Id, OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, fixture.FileStorage.DeleteCalls);
    }

    [Fact]
    public async Task HandleAsOperatorAsync_WhenStorageIsUnavailable_StillDeletesTheRow_AndSucceeds()
    {
        var fixture = CreateFixture();
        fixture.FileStorage.ThrowUnavailableOnDelete = true;

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new DeleteAttachmentAsOperator(fixture.Attachment.Id, OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var reloaded = await fixture.Attachments.GetByIdAsync(fixture.Attachment.Id, CancellationToken.None);
        Assert.Equal(AttachmentState.Deleted, reloaded!.State);
    }

    [Fact]
    public async Task HandleAsOperatorAsync_WhenTheAttachmentDoesNotExist_ReturnsNotFound()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new DeleteAttachmentAsOperator(new AttachmentId(Guid.NewGuid()), OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Attachment.NotFound", result.Error!.Value.Code);
    }
}
