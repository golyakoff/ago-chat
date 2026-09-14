using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Logging;

namespace Ago.Chat.Application.UseCases.BulkDeleteSiteAttachments;

/// <summary>
/// `23-80`: "select many, delete once... a deleted attachment must leave an honest transcript." The
/// per-attachment mechanics are the identical two steps `DeleteAttachmentHandler` (`5-08`) already
/// uses - <see cref="Domain.Attachment.MarkDeleted"/>, then a best-effort storage-object delete - reused
/// rather than called (that handler is gated on <see cref="Permission.AttachmentDelete"/>, a different
/// permission for a different screen; see this class's own file for why a second, `SiteConfigure`-gated
/// handler is the right shape rather than a second permission check bolted onto the first).
///
/// <para><b>Also releases <see cref="ISiteAttachmentStorageBudget"/>'s reservation - `5-08`'s own
/// delete does not.</b> Found while building this item's own "the space freed" promise: a deleted
/// attachment must actually shrink <c>sites.attachment_bytes_reserved</c> for "43 files, 312 MB freed"
/// to be true, and reading <c>DeleteAttachmentHandler</c> shows it never calls
/// <see cref="ISiteAttachmentStorageBudget.ReleaseAsync"/> at all - every attachment `5-08`'s own route
/// has ever deleted is still counted against that tenant's quota today. Fixing that latent gap in
/// `5-08`'s own handler is out of this item's scope (CLAUDE.md: "`23-76` is already done - do not touch
/// its own scope," and `5-08` is a different, already-shipped item entirely) - flagged in this change's
/// own report as a defect worth its own ticket, not silently patched here. What *is* in scope is not
/// repeating the gap in new code: this handler releases the reservation for every attachment it
/// actually deletes, inside the same transaction as the row's own state change.</para>
///
/// <para><b>Per-attachment atomicity, not one transaction for the whole batch.</b> A batch of forty
/// selected files where the thirty-first hits a transient DB error should not silently discard the
/// thirty already committed - the same "some succeed, the response says which" shape a bulk operation
/// in this codebase should have, and the only one this port can express without a second, wider
/// transaction abstraction the rest of Application does not need. The storage-object delete for each
/// attachment happens strictly after its own row's transaction commits, the identical ordering
/// <c>DeleteAttachmentHandler</c>'s own remarks give for why (the row is the source of truth every
/// reader already trusts; a storage-side leak on a rare I/O failure is tolerated the same way, logged
/// rather than propagated).</para>
/// </summary>
public sealed class BulkDeleteSiteAttachmentsHandler(
    IAttachmentRepository attachments,
    IFileStorage fileStorage,
    ISiteAttachmentStorageBudget siteBudget,
    IUnitOfWork unitOfWork,
    IPermissionChecker permissions,
    ILogger<BulkDeleteSiteAttachmentsHandler> logger)
{
    internal const int MaxBatchSize = 200;

    public async Task<Result<BulkDeleteSiteAttachmentsResult>> HandleAsync(
        BulkDeleteSiteAttachments command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to delete this site's attachments.");
        }

        if (command.AttachmentIds.Count > MaxBatchSize)
        {
            return ConversationErrors.AttachmentBulkDeleteTooMany(command.AttachmentIds.Count, MaxBatchSize);
        }

        var deletedCount = 0;
        var freedBytes = 0L;
        var alreadyGoneCount = 0;
        var notFoundIds = new List<Guid>();

        foreach (var attachmentId in command.AttachmentIds)
        {
            var attachment = await attachments.GetByIdAsync(attachmentId, cancellationToken);
            if (attachment is null || attachment.SiteId != command.SiteId)
            {
                // The same info-hiding shape DeleteAttachmentHandler's own remarks establish - a
                // wrong-tenant id must read identically to a nonexistent one, proven by
                // BulkDeleteAttachmentsAcrossTenantsIsRefusedTests rather than left to this comment.
                notFoundIds.Add(attachmentId.Value);
                continue;
            }

            if (attachment.State != AttachmentState.Ready)
            {
                // Already Deleted (a previous delete, from either route), or still Pending and never a
                // real held file - neither is this handler's to act on a second time.
                alreadyGoneCount++;
                continue;
            }

            var sizeBytes = attachment.SizeBytes;
            var objectKey = attachment.ObjectKey;
            var thumbnailKey = attachment.ThumbnailKey;

            await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
            {
                attachment.MarkDeleted();
                await attachments.SaveAsync(attachment, cancellationToken);
                await siteBudget.ReleaseAsync(command.SiteId, sizeBytes, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            await TryDeleteObjectAsync(attachmentId, objectKey, cancellationToken);
            if (thumbnailKey is not null)
            {
                await TryDeleteObjectAsync(attachmentId, thumbnailKey, cancellationToken);
            }

            deletedCount++;
            freedBytes += sizeBytes;
        }

        return new BulkDeleteSiteAttachmentsResult(deletedCount, freedBytes, notFoundIds, alreadyGoneCount);
    }

    /// <summary>Identical shape to <c>DeleteAttachmentHandler.TryDeleteObjectAsync</c> - not extracted
    /// to a shared helper because the two live in different use cases with no common base today, and a
    /// four-line try/catch is not worth a new shared type for.</summary>
    private async Task TryDeleteObjectAsync(AttachmentId attachmentId, string objectKey, CancellationToken cancellationToken)
    {
        try
        {
            await fileStorage.DeleteAsync(new ObjectKey(objectKey), cancellationToken);
        }
        catch (FileStorageUnavailableException ex)
        {
            logger.LogWarning(
                ex,
                "Bulk-deleted attachment row {AttachmentId} but could not delete its storage object {ObjectKey}; " +
                "it may now be an orphan.",
                attachmentId.Value, objectKey);
        }
    }
}
