using Ago.Chat.Contracts;
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
/// <para><b>The archive.</b> `16-02`'s brief is explicit that erasure must reach `adr/0031`'s archive
/// once one exists. Nothing archives today - `13-06`, the real archive writer, is not built
/// (`Ago.Chat.Worker`'s <c>IMessageArchiveGate</c> is used only by <c>MessagePartitionPruneJob</c>'s
/// time-based whole-partition drops, a different mechanism from this row-scoped delete). This job does
/// not reach an archive because there is nothing to reach - stated here rather than built
/// speculatively (a fake archive-erasure port with no real implementation is exactly the premature
/// generalisation `CLAUDE.md` warns against). When `13-06` ships a real archive, the obvious single
/// place to add "and delete the archived copy too" is <see cref="EraseConversationAsync"/>, as a
/// clearly-commented step between the MinIO deletes and the message-batch loop below.</para>
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

        IReadOnlyList<Guid> pending;
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            pending = await ConversationErasureQuery.ListPendingAsync(connection, options.Value.BatchSize, cancellationToken);
        }

        var erased = 0;
        foreach (var conversationId in pending)
        {
            try
            {
                if (await EraseConversationAsync(conversationId, cancellationToken))
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
                    conversationId);
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
    /// </list>
    /// </summary>
    /// <returns><see langword="true"/> if this conversation was fully erased (its row is gone);
    /// <see langword="false"/> if it was only partially drained this call and remains flagged for the
    /// next cycle.</returns>
    internal async Task<bool> EraseConversationAsync(Guid conversationId, CancellationToken cancellationToken)
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
                connection, conversationId, options.Value.MessageBatchSize, cancellationToken);

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
