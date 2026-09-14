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

    private const long AttachmentSizeBytes = 1024;

    private sealed record Fixture(
        DeleteAttachmentHandler Handler,
        FakeAttachmentRepository Attachments,
        FakeFileStorage FileStorage,
        FakeSiteAttachmentStorageBudget SiteBudget,
        FakeUnitOfWork UnitOfWork,
        Attachment Attachment);

    private static Fixture CreateFixture(
        bool grantPermission = true, AttachmentState state = AttachmentState.Ready, SiteId? attachmentSiteId = null,
        bool withThumbnail = false, long reservedBytes = AttachmentSizeBytes)
    {
        var attachments = new FakeAttachmentRepository();
        var attachment = Attachment.CreatePending(
            new AttachmentId(Guid.NewGuid()), attachmentSiteId ?? SiteId, new ConversationId(Guid.NewGuid()),
            "site/x/conv/y/z.png", "image/png", AttachmentSizeBytes, Now);
        if (state == AttachmentState.Ready)
        {
            attachment.ConfirmReady(AttachmentSizeBytes, "image/png", Now);
            if (withThumbnail)
            {
                attachment.SetThumbnail("site/x/conv/y/z_thumb.jpg");
            }
        }
        else if (state == AttachmentState.Deleted)
        {
            attachment.ConfirmReady(AttachmentSizeBytes, "image/png", Now);
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

        // Seeded at the attachment's own size by default - the same "already reserved for this one
        // attachment before the test starts" baseline BulkDeleteSiteAttachmentsHandler's own tests
        // would need, so a release test can show the reservation actually shrink rather than merely
        // stay at zero (which a broken handler that never reserved anything would also produce).
        var siteBudget = new FakeSiteAttachmentStorageBudget();
        siteBudget.SeedReserved(attachmentSiteId ?? SiteId, reservedBytes);

        var unitOfWork = new FakeUnitOfWork();

        var handler = new DeleteAttachmentHandler(
            attachments, fileStorage, siteBudget, unitOfWork, permissions, NullLogger<DeleteAttachmentHandler>.Instance);
        return new Fixture(handler, attachments, fileStorage, siteBudget, unitOfWork, attachment);
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
    public async Task HandleAsOperatorAsync_WhenTheOperatorHoldsThePermission_ReleasesTheSiteBudgetReservationForTheAttachmentsOwnSize()
    {
        // `25-79`: fails before the fix - with the release call removed from the handler, this seeded
        // reservation stays at AttachmentSizeBytes after the delete instead of shrinking to zero, and
        // ReleaseCalls stays empty. Restored, it shrinks by exactly the attachment's own SizeBytes.
        var fixture = CreateFixture(reservedBytes: AttachmentSizeBytes);

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new DeleteAttachmentAsOperator(fixture.Attachment.Id, OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var release = Assert.Single(fixture.SiteBudget.ReleaseCalls);
        Assert.Equal(SiteId, release.SiteId);
        Assert.Equal(AttachmentSizeBytes, release.Bytes);
        Assert.Equal(0, fixture.SiteBudget.ReservedFor(SiteId));
        // Released inside the same commit as the row's own state change - the identical
        // BulkDeleteSiteAttachmentsHandler shape, proven the same way that handler's own transaction
        // would be: one BeginTransactionAsync, one CommitAsync, for this one attachment.
        Assert.Equal(1, fixture.UnitOfWork.TransactionsBegun);
        Assert.Equal(1, fixture.UnitOfWork.TransactionsCommitted);
    }

    [Fact]
    public async Task HandleAsOperatorAsync_WhenTheSiteHadOtherBytesAlreadyReserved_ReleasesOnlyThisAttachmentsOwnSize()
    {
        // A stricter version of the test above: the site's reservation includes other attachments'
        // bytes too, so "shrinks to zero" would not distinguish a correct release from one that wiped
        // the whole reservation. Only this attachment's own 1024 bytes must come off.
        var fixture = CreateFixture(reservedBytes: 5000);

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new DeleteAttachmentAsOperator(fixture.Attachment.Id, OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(5000 - AttachmentSizeBytes, fixture.SiteBudget.ReservedFor(SiteId));
    }

    [Fact]
    public async Task HandleAsOperatorAsync_WhenAlreadyDeleted_IsIdempotent_AndDoesNotReleaseTheBudgetASecondTime()
    {
        // The one way this fix could itself introduce a bug: a retried delete against an
        // already-Deleted attachment (the handler's own pre-existing idempotent early-return, just
        // above the release this item adds) must not release the budget twice for one attachment.
        var fixture = CreateFixture(state: AttachmentState.Deleted, reservedBytes: 5000);

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new DeleteAttachmentAsOperator(fixture.Attachment.Id, OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(fixture.SiteBudget.ReleaseCalls);
        Assert.Equal(5000, fixture.SiteBudget.ReservedFor(SiteId));
        Assert.Equal(0, fixture.UnitOfWork.TransactionsBegun);
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
