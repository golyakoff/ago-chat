using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `16-02`: erases one conversation and everything under it - the atomic erasure unit both a direct
/// conversation-erasure request and a whole-site erasure eventually converge on (see
/// <see cref="SiteErasureJob"/>'s own remarks on why it drives this job's *ticks*, not this job's
/// *method*, to avoid it).
///
/// Same `PeriodicTimer`/`BackgroundService` shape as <see cref="OutboxPruneJob"/>/
/// <see cref="AttachmentOrphanSweepJob"/>: runs once immediately, then every
/// <see cref="ConversationErasureJobOptions.Interval"/>, and a transient failure logs and retries next
/// tick rather than killing the backstop (`concurrency.md`). One conversation's failure does not stop
/// the others claimed in the same cycle - see <see cref="SweepAsync"/>'s own per-item try/catch.
///
/// <para><b>The archive, closed by `24-09`.</b> `16-02`'s brief was explicit that erasure must reach
/// `adr/0031`'s archive once one existed - it did not yet when this job was written, and this
/// paragraph used to say so ("nothing archives today"). `13-06` shipped the real archive writer since,
/// and left the seam this job's own remarks named as the obvious next step: <see cref="EraseConversationAsync"/>
/// now calls <see cref="ConversationArchiveEraser.EraseAsync"/> for every conversation it erases, which
/// downloads each of the site's archived periods, drops the lines naming this conversation, and
/// re-uploads the result - a read-modify-write, not a delete, since one archive object covers every
/// conversation a site had in one period (`docs/adr/0108-*`, <see cref="ConversationArchiveEraser"/>'s
/// own remarks for the full reasoning). <b>This changes this job's reliability profile</b>: erasure now
/// depends on object storage being reachable for a read as well as a write, and can take one HTTP
/// round trip per archived period the site has, not only per attachment. A failure in that step is
/// allowed to throw rather than being logged and tolerated - unlike an attachment-object delete, a
/// silently-tolerated archive failure would let this method report the conversation erased while an
/// archived copy still stood, which is precisely the defect this item exists to close.</para>
///
/// <para><b>Completeness and backups.</b> Erasure here means every row and object this process can
/// reach. `15-02`/`adr/0050` already set backup retention to <b>30 days</b> - this item does not
/// additionally build a deletion journal replayed after restore (deliberately rejected: such a journal
/// is itself a list of people who asked to be forgotten, `personal-data.md`'s own reasoning). Erasure
/// is complete, in full, once that 30-day window has passed.</para>
/// </summary>
public sealed class ConversationErasureJob(
    NpgsqlDataSource dataSource,
    IFileStorage fileStorage,
    ConversationArchiveEraser archiveEraser,
    IClock clock,
    IOptions<ConversationErasureJobOptions> options,
    ILogger<ConversationErasureJob> logger) : BackgroundService
{
    private const string TableTag = "conversations_erasure";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.Interval);
        do
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Conversation erasure cycle failed; retrying next cycle.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One bounded pass. <c>internal</c> so an integration test can drive exactly one cycle
    /// against a real Postgres/MinIO instead of waiting for a timer - the same seam
    /// <see cref="AttachmentOrphanSweepJob.SweepAsync"/>/<see cref="DemoTenantExpiryJob.SweepAsync"/>
    /// already expose for the same reason.</summary>
    internal async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var startedAt = clock.UtcNow;

        IReadOnlyList<PendingConversationErasure> pending;
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            pending = await ConversationErasureQuery.ListPendingAsync(connection, options.Value.BatchSize, cancellationToken);
        }

        var erased = 0;
        foreach (var candidate in pending)
        {
            try
            {
                if (await EraseConversationAsync(
                    candidate.ConversationId, candidate.SiteId, candidate.VisitorId, cancellationToken))
                {
                    erased++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One conversation's failure must not stop the others claimed in this cycle - the
                // same reasoning DemoTenantExpiryJob.SweepAsync's own per-tenant try/catch gives.
                logger.LogError(
                    ex, "Failed to erase conversation {ConversationId}; it stays flagged for the next cycle.",
                    candidate.ConversationId);
            }
        }

        if (erased > 0)
        {
            logger.LogInformation("Conversation erasure removed {Count} conversation(s) and everything under them.", erased);
        }

        ChatMetrics.RecordRetentionPruneCycle(TableTag, erased, clock.UtcNow - startedAt);
        return erased;
    }

    /// <summary>
    /// <b>The order is the design</b>, the same reasoning <c>DemoTenantExpiryJob.RemoveAsync</c>'s own
    /// remarks give for a whole tenant, applied to one conversation:
    /// <list type="bullet">
    /// <item><b>MinIO objects first</b> - after the attachment rows are gone there is nothing left to
    /// enumerate, and the bytes would orphan forever, which is exactly the gap `personal-data.md`
    /// records for conversation deletion today and this item exists to close.</item>
    /// <item><b>Messages, in bounded batches</b> - one conversation's history can be large
    /// (`16-02`'s own instruction), so this is a loop of bounded `DELETE`s
    /// (<see cref="ConversationErasureQuery.DeleteMessageBatchAsync"/>), not one unbounded statement.
    /// Bounded by <see cref="ConversationErasureJobOptions.MaxMessageBatchesPerConversation"/> as well
    /// as batch size: a conversation not fully drained within that bound is left exactly as it was -
    /// still flagged, no attachments or the row itself touched yet - and the next cycle's claim finds
    /// it again and continues where this one left off.</item>
    /// <item><b>Attachment rows, then the conversation row</b> - only once every message is confirmed
    /// gone, so nothing can still reference an attachment that is about to disappear.</item>
    /// <item><b>`23-08`: the visitor's own contact details</b>, deleted alongside the notes and tags -
    /// see <see cref="ConversationErasureQuery.DeleteContactDetailsForVisitorAsync"/>'s own remarks for
    /// why this reaches every contact detail the visitor has, not only ones tied to this conversation.
    /// A person's erasure request takes the conversation <i>and</i> the contact - it is all their data
    /// (`docs/design/decisions.md` §4) - which is why this step lives here, in the request-driven path,
    /// and deliberately has no counterpart in <c>MessagePartitionPruneJob</c>'s retention sweep: that
    /// job ages out transcripts on a timer with no request behind it, and sweeping a contact away with
    /// an aged-out conversation would cost the tenant an asset every time a transcript's own window
    /// closed - the "no cascade from retention" half of the same decision.</item>
    /// <item><b>`24-09`: the archive, last of all and still before the conversation row.</b>
    /// <see cref="ConversationArchiveEraser"/> reads only <c>message_archives</c> and this conversation's
    /// id - it needs nothing from <c>messages</c> that survives the batch loop above, so it could run
    /// anywhere before the row delete. It runs here, immediately before
    /// <see cref="ConversationErasureQuery.DeleteConversationAsync"/>, for the reason that placement is
    /// load-bearing rather than cosmetic: <c>erasure_requested_at</c> lives on the <c>conversations</c>
    /// row, so once that row is gone there is nothing left for <see cref="ListPendingAsync"/> to
    /// re-claim. If archive erasure ran <i>after</i> the row delete and then failed or the process died,
    /// the next cycle would never look at this conversation again, and an archived copy could survive
    /// with no flag anywhere pointing at it - the exact "row deleted before its object, leaving an
    /// orphan nobody can find" hazard <c>personal-data.md</c> already names for attachments, applied to
    /// the archive instead. Running it here means a crash or a storage failure leaves the row (and the
    /// flag) standing, and the next cycle's claim retries the whole conversation - messages and notes
    /// and tags already gone are idempotent no-ops, and only the archive step (and the row delete) is
    /// left to actually do anything.</item>
    /// </list>
    /// </summary>
    /// <returns><see langword="true"/> if this conversation was fully erased (its row is gone);
    /// <see langword="false"/> if it was only partially drained this call and remains flagged for the
    /// next cycle.</returns>
    internal async Task<bool> EraseConversationAsync(
        Guid conversationId, Guid siteId, Guid visitorId, CancellationToken cancellationToken)
    {
        await using (var readConnection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            var objectKeys = await ConversationErasureQuery.ListAttachmentObjectKeysAsync(
                readConnection, conversationId, cancellationToken);
            foreach (var key in objectKeys)
            {
                try
                {
                    // Idempotent on the storage side (5-02's own "S3 DELETE is idempotent") - a
                    // retried erasure of a conversation whose objects are already gone is a no-op.
                    await fileStorage.DeleteAsync(new ObjectKey(key), cancellationToken);
                }
                catch (FileStorageUnavailableException ex)
                {
                    // The same tolerance AttachmentOrphanSweepJob already applies: a storage hiccup
                    // must not abandon the whole conversation's erasure, and the residual is an
                    // orphaned object rather than a stuck flag nothing ever retries. Logged, not
                    // swallowed - a leak nobody can see is how a gap like this stays unnoticed.
                    logger.LogWarning(
                        ex,
                        "Could not delete storage object {ObjectKey} for conversation {ConversationId} being erased; it may now be an orphan.",
                        key, conversationId);
                }
            }
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        for (var batch = 0; batch < options.Value.MaxMessageBatchesPerConversation; batch++)
        {
            var removed = await ConversationErasureQuery.DeleteMessageBatchAsync(
                connection, conversationId, siteId, options.Value.MessageBatchSize, cancellationToken);

            if (removed < options.Value.MessageBatchSize)
            {
                // Fewer than requested means this was the last batch - every message is gone, so the
                // rest of this conversation's teardown can proceed in the same call.
                await ConversationErasureQuery.DeleteAttachmentsAsync(connection, conversationId, cancellationToken);
                // `18-04`: notes and tag associations - both personal data about this conversation
                // (ConversationNote's own remarks), removed the same way attachments are, before the
                // conversation row itself. Tag *definitions* are never touched here - see
                // ConversationErasureQuery.DeleteTagsForConversationAsync's own remarks.
                await ConversationErasureQuery.DeleteNotesForConversationAsync(connection, conversationId, cancellationToken);
                await ConversationErasureQuery.DeleteTagsForConversationAsync(connection, conversationId, cancellationToken);
                // `23-08`: the visitor's own contact details - see this method's own remarks above and
                // DeleteContactDetailsForVisitorAsync's for why this is keyed to the visitor rather than
                // this conversation.
                await ConversationErasureQuery.DeleteContactDetailsForVisitorAsync(connection, visitorId, cancellationToken);
                // `24-09`: strip this conversation's own rows out of every archive object the site has
                // that might still hold them - see this method's own remarks for why this must run
                // before the conversation row goes, not after.
                await archiveEraser.EraseAsync(new SiteId(siteId), conversationId, cancellationToken);
                await ConversationErasureQuery.DeleteConversationAsync(connection, conversationId, cancellationToken);
                return true;
            }
        }

        // MaxMessageBatchesPerConversation exhausted without draining - an exceptionally large
        // conversation. Leave everything else untouched; the next cycle's claim re-finds this
        // conversation (erasure_requested_at is still set) and continues.
        logger.LogInformation(
            "Conversation {ConversationId} was not fully drained within {MaxBatches} message batch(es) this cycle; it stays flagged and will continue next cycle.",
            conversationId, options.Value.MaxMessageBatchesPerConversation);
        return false;
    }
}
