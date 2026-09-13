using System.Security.Cryptography;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;

namespace Ago.Chat.Worker;

/// <summary>
/// `23-76`: "the same bytes uploaded repeatedly cost one object, within a tenant" - the actual work,
/// reacting to `AttachmentConfirmed` exactly the way <see cref="AttachmentThumbnailGenerator"/> already
/// does (same event, a different, independent Worker consumer - see
/// <see cref="AttachmentDeduplicationConsumer"/>'s own remarks on why a second `Competing` subscription
/// on the identical event is the right shape, not a change to the first one).
///
/// <para><b>Why this runs here, not inline in <c>ConfirmAttachmentHandler</c>.</b> A real, byte-verified
/// hash needs the actual object bytes - <c>IFileStorage.GetMetadataAsync</c> (the confirm request's own
/// HEAD-verify) exposes only <c>SizeBytes</c>/<c>ContentType</c>, no checksum, and extending it to carry
/// one would be a platform-port change (`Ago.Platform.Abstractions.ObjectMetadata`) outside a
/// single-repository `ago-chat` branch's own reach - the identical "deferred, not hidden" gap
/// `file-storage.md` already names for `CreateDownloadUrlAsync`'s missing `Content-Disposition`
/// override. Fetching the bytes inline would also add a full download's latency to the confirm request
/// itself, on top of that - asynchronous, off the request path, is strictly better here even setting the
/// platform-port question aside.
///
/// <para><b>Download-and-hash reuses `AttachmentThumbnailGenerator`'s own already-established
/// precedent, not a new exception to `file-storage.md`'s own rule.</b> That job already downloads a
/// confirmed attachment's full bytes, through a presigned URL and a bare <see cref="HttpClient"/>
/// (`adr/0008`: <see cref="IFileStorage"/> is presign-only by design, so this is calling it the same
/// way any other consumer of the port does, never a raw storage-SDK call) - bounded by
/// <see cref="AttachmentOptions.MaxSizeBytes"/> (5 MiB today), run once per confirmed attachment, in the
/// Worker, off the request path. This class does the identical download for the identical reason
/// (server-side content it must actually inspect), the only difference being what it computes from the
/// bytes once they arrive.</para>
/// </summary>
public sealed class AttachmentDeduplicator(
    IAttachmentRepository attachments,
    ISiteAttachmentStorageBudget siteBudget,
    IFileStorage fileStorage,
    ILogger<AttachmentDeduplicator> logger)
{
    // Ephemeral, immediately-consumed URL for the Worker's own internal transfer - the identical
    // reasoning AttachmentThumbnailGenerator's own UrlLifetime gives for its own download.
    private static readonly TimeSpan UrlLifetime = TimeSpan.FromMinutes(2);
    private static readonly HttpClient Http = new();

    public async Task DeduplicateAsync(AttachmentId attachmentId, string objectKey, CancellationToken cancellationToken)
    {
        var attachment = await attachments.GetByIdAsync(attachmentId, cancellationToken);
        if (attachment is null)
        {
            logger.LogWarning(
                "AttachmentConfirmed for {AttachmentId} but the row no longer exists; skipping deduplication.", attachmentId.Value);
            return;
        }

        // Idempotency, the identical read-then-write shape AttachmentThumbnailGenerator's own
        // `ThumbnailKey is not null` guard uses, safe for the same reason (RabbitMQ `Competing` never
        // delivers the same message to two consumers simultaneously - a redelivery only ever follows a
        // known ack/nack outcome).
        if (attachment.ContentHash is not null)
        {
            return;
        }

        var downloadUrl = await fileStorage.CreateDownloadUrlAsync(new ObjectKey(objectKey), UrlLifetime, cancellationToken);
        var contentHash = await ComputeSha256Async(downloadUrl, cancellationToken);

        var duplicate = await attachments.FindReadyDuplicateAsync(attachment.SiteId, contentHash, attachment.Id, cancellationToken);
        if (duplicate is null)
        {
            // First copy of these bytes this tenant has - just record the hash for a future upload to
            // match against. No object-store or budget change: this attachment's own object is the
            // canonical one now.
            attachment.SetContentHash(contentHash);
            await attachments.SaveAsync(attachment, cancellationToken);
            return;
        }

        // Duplicate: repoint this row at the existing object, release the site's storage reservation
        // for these bytes (the tenant's disk cost just dropped to zero net new bytes - see
        // `ISiteAttachmentStorageBudget`'s own remarks on why the *conversation's* own reservation is
        // deliberately left untouched), save first, delete the now-redundant object second. That
        // order matters: a crash between "save" and "delete" leaks one harmless orphan object nothing
        // points at any more (the orphan sweep's own domain - `Pending` rows, not `Ready` ones, so this
        // is a small, one-time miss, not an ongoing leak); the reverse order (delete first) would risk
        // losing the only copy of the bytes if the save that repoints this row never lands.
        attachment.PointToExistingObject(duplicate.ObjectKey, contentHash);
        await attachments.SaveAsync(attachment, cancellationToken);
        await siteBudget.ReleaseAsync(attachment.SiteId, attachment.SizeBytes, cancellationToken);

        try
        {
            await fileStorage.DeleteAsync(new ObjectKey(objectKey), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The attachment row already points at the surviving object - a failed delete here leaks
            // one redundant object in storage, never a correctness problem (nothing references
            // `objectKey` any more). Logged, not rethrown: retrying this whole method via redelivery
            // would re-run the dedup lookup pointlessly (attachment.ContentHash is already set by the
            // time a retry could observe it, so the idempotency guard above would just skip it,
            // leaving the object never cleaned up at all) - a real orphaned-object sweep for this case
            // is a real gap, not one this item's own Scope (a storage ceiling and a dedup rule) covers.
            logger.LogWarning(
                ex, "Deduplicated attachment {AttachmentId} but failed to delete its redundant object {ObjectKey}.",
                attachmentId.Value, objectKey);
        }
    }

    private static async Task<string> ComputeSha256Async(Uri downloadUrl, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var hashBytes = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hashBytes);
    }
}
