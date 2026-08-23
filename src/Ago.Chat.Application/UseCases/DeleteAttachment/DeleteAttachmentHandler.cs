using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Logging;

namespace Ago.Chat.Application.UseCases.DeleteAttachment;

/// <summary>
/// `5-08`: the moderation action paired with the admin role (`authorization.md`) - the one new
/// endpoint this item adds to `5-03`'s attachment surface, scoped here rather than there because it
/// had no reason to exist before an admin role could hold the permission it checks.
///
/// Deletes the row (a terminal state transition, <see cref="Domain.Attachment.MarkDeleted"/>) and the
/// storage object, in that order - the row is the source of truth a reader (`GetAttachmentDownloadUrlHandler`,
/// `MessageBatchWriter`'s "must be `Ready`" check) already trusts, so it must flip to `Deleted` before
/// the bytes are gone, not after; a crash between the two steps leaves an orphaned object, exactly the
/// gap `5-04`'s sweep job... does not cover (the sweeper only claims expired `Pending` rows, never a
/// `Deleted` one) - accepted here the same way file-storage.md already accepts it for a confirm that
/// never links to a message: a rare, storage-side-only leak, not a correctness bug, and a second sweep
/// query for it is not justified by anything this item's own scope asks for.
/// </summary>
public sealed class DeleteAttachmentHandler(
    IAttachmentRepository attachments,
    IFileStorage fileStorage,
    IPermissionChecker permissions,
    ILogger<DeleteAttachmentHandler> logger)
{
    public async Task<Result> HandleAsOperatorAsync(DeleteAttachmentAsOperator command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.AttachmentDelete, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to delete attachments for this site.");
        }

        var attachment = await attachments.GetByIdAsync(command.AttachmentId, cancellationToken);
        if (attachment is null || attachment.SiteId != command.SiteId)
        {
            // Same info-hiding shape GetAttachmentDownloadUrlHandler already uses for a missing
            // conversation: an attachment belonging to a different site must read identically to one
            // that does not exist, never leak "it exists, just not yours" to an operator token scoped
            // to a different tenant.
            return ConversationErrors.AttachmentNotFound(command.AttachmentId.Value);
        }

        if (attachment.State == AttachmentState.Deleted)
        {
            // Idempotent, deliberately - the same "tolerate already-gone" reasoning 5-04's orphan
            // sweeper applies to the storage object extended to the row itself: a retried delete (a
            // double-click, or a retry after a dropped response to an already-successful call) must
            // succeed quietly, not surface as an error the console has no good way to explain.
            return Result.Success();
        }

        attachment.MarkDeleted();
        await attachments.SaveAsync(attachment, cancellationToken);

        await TryDeleteObjectAsync(attachment.Id, attachment.ObjectKey, cancellationToken);

        // `5-04`'s thumbnail job (AttachmentThumbnailConsumer, Ago.Chat.Worker) may have already
        // populated this - found live while manually verifying this item: deleting only
        // `ObjectKey` left a real `_thumb.jpg` behind in MinIO forever, since nothing else in this
        // codebase ever deletes a thumbnail (the orphan sweeper only claims expired `Pending` rows,
        // never a `Deleted` one - this type's own remarks already say so for the main object; the
        // same gap applies to the thumbnail and was not obvious from reading the code alone).
        if (attachment.ThumbnailKey is { } thumbnailKey)
        {
            await TryDeleteObjectAsync(attachment.Id, thumbnailKey, cancellationToken);
        }

        return Result.Success();
    }

    private async Task TryDeleteObjectAsync(AttachmentId attachmentId, string objectKey, CancellationToken cancellationToken)
    {
        try
        {
            // Idempotent on the storage side too (5-02's own test: "S3 DELETE is idempotent - no
            // exception expected") - copied from AttachmentOrphanSweepJob's own try/catch shape
            // (Ago.Chat.Worker), the only other caller of IFileStorage.DeleteAsync in this codebase.
            await fileStorage.DeleteAsync(new ObjectKey(objectKey), cancellationToken);
        }
        catch (FileStorageUnavailableException ex)
        {
            logger.LogWarning(
                ex,
                "Deleted attachment row {AttachmentId} but could not delete its storage object {ObjectKey}; " +
                "it may now be an orphan.",
                attachmentId.Value, objectKey);
        }
    }
}
