using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `24-13`: the write-only counterpart to <c>ExportRequestRepository</c> for `erasure_records` -
/// raw Npgsql, in `Ago.Chat.Worker` rather than behind an `Ago.Chat.Application` port, the same
/// "background housekeeping speaks SQL directly" shape <see cref="ConversationErasureQuery"/>/
/// <see cref="SiteErasureQuery"/>/<see cref="OutboxPruneQuery"/> already establish: these are not
/// user-facing use cases funnelled through a handler, they are a job's own internal bookkeeping, and
/// <see cref="IErasureRequestRepository"/> already covers the one place this table's rows are
/// genuinely created from a request (`ErasureRequestRepository`'s own remarks).
///
/// <para><b>Idempotent by construction, the same "must not grow a row per attempt" requirement the
/// background-worker brief for this item states explicitly.</b> Every method here is an `UPDATE`
/// against a row that already exists (inserted once, by <c>ErasureRequestRepository</c>, at request
/// time) - never an `INSERT`. A conversation or site that takes several sweep cycles to drain updates
/// the *same* row every cycle; a cycle that fails updates it again. One receipt, however many
/// attempts.</para>
///
/// <para><b>Why <see cref="AddMessagesDeletedAsync"/> exists separately from the two completion
/// methods.</b> Every other per-step count (attachments, notes, tags, contact details, storage
/// objects, identities) is produced exactly once, in the single cycle that finishes the conversation
/// or site - <c>ConversationErasureJob.EraseConversationAsync</c>'s own ordering only runs those steps
/// in its finishing branch. Messages are different: `MaxMessageBatchesPerConversation` bounds how many
/// batches one cycle deletes, so an exceptionally large conversation drains across several cycles, and
/// each of those non-finishing cycles has already deleted real rows this receipt must not lose. This
/// method adds to a running total; the others below set an absolute count, because they are only ever
/// called once, in the cycle that actually produced that count.</para>
///
/// <para><b>Why nothing here ever changes a row already <c>Completed</c>.</b> Every statement's
/// `WHERE` clause excludes `status = 'Completed'` - once every row and object an erasure can reach is
/// confirmed gone, nothing later has anything left to add or fail. Without that guard, a crash that
/// left `DeleteConversationAsync` unrun after this file's own completion update would let a harmless
/// idempotent replay (every later delete finds nothing, by construction) walk back in and re-add
/// already-counted messages on top of themselves.</para>
///
/// <para><b>`Completed` means "every row and object this process could reach is gone" - it does not
/// mean the erased data has left every copy this system holds.</b> `adr/0050` backs up both Postgres
/// databases and the MinIO bucket daily and keeps the collected copies for **30 days**; a restore
/// performed inside that window can still contain exactly what a `Completed` record here describes as
/// erased. `personal-data.md`'s own "Deletion versus backups" resolves that by bounding the window
/// rather than chasing every copy with a deletion journal - the correct reading of `Completed` is "the
/// live system, right now" plus that stated 30-day trailing exposure, not "gone everywhere,
/// immediately". A record claiming the second, stronger thing would be the dishonest version of this
/// item.</para>
/// </summary>
public static class ErasureRecordQuery
{
    // ---------------------------------------------------------------------------------------------
    // Conversation scope
    // ---------------------------------------------------------------------------------------------

    /// <summary>Adds this cycle's own message count to the running total on a still-open (not yet
    /// <c>Completed</c>) record - called at the end of a cycle that did not finish the conversation.
    /// A no-op, on purpose, when <paramref name="recordId"/> is <see langword="null"/> - a
    /// site-cascaded conversation carries no <c>ErasureRecordId</c> of its own
    /// (<see cref="ConversationConfiguration"/>'s own remarks).</summary>
    public static async Task AddMessagesDeletedAsync(
        NpgsqlConnection connection, Guid? recordId, int messagesDeletedThisCycle, CancellationToken cancellationToken)
    {
        if (recordId is null || messagesDeletedThisCycle == 0)
        {
            return;
        }

        await using var command = new NpgsqlCommand(
            "update erasure_records set messages_deleted = messages_deleted + @delta where id = @id and status <> 'Completed'",
            connection);
        command.Parameters.AddWithValue("id", recordId.Value);
        command.Parameters.AddWithValue("delta", messagesDeletedThisCycle);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The finishing cycle: adds the last batch's own messages to the running total, sets
    /// every other conversation-scope count outright (accurate in one call - see this file's own
    /// remarks), and marks the record <c>Completed</c>. Called before
    /// <see cref="ConversationErasureQuery.DeleteConversationAsync"/>, never after - the same
    /// "the record must still be reachable by <c>id</c> when this runs" ordering
    /// <c>ConversationErasureJob</c>'s own remarks give for running the archive step before the row
    /// delete, applied to this write instead.</summary>
    public static async Task CompleteConversationErasureAsync(
        NpgsqlConnection connection, Guid? recordId, int messagesDeletedThisCycle, int attachmentsDeleted,
        int storageObjectsDeleted, int notesDeleted, int tagsDeleted, int contactDetailsDeleted,
        DateTimeOffset completedAt, CancellationToken cancellationToken)
    {
        if (recordId is null)
        {
            return;
        }

        await using var command = new NpgsqlCommand(
            """
            update erasure_records
            set status = 'Completed',
                completed_at = @completedAt,
                failure_reason = null,
                messages_deleted = messages_deleted + @messagesDelta,
                attachments_deleted = @attachments,
                storage_objects_deleted = @objects,
                notes_deleted = @notes,
                tags_deleted = @tags,
                contact_details_deleted = @contacts
            where id = @id and status <> 'Completed'
            """,
            connection);
        command.Parameters.AddWithValue("id", recordId.Value);
        command.Parameters.AddWithValue("completedAt", completedAt);
        command.Parameters.AddWithValue("messagesDelta", messagesDeletedThisCycle);
        command.Parameters.AddWithValue("attachments", attachmentsDeleted);
        command.Parameters.AddWithValue("objects", storageObjectsDeleted);
        command.Parameters.AddWithValue("notes", notesDeleted);
        command.Parameters.AddWithValue("tags", tagsDeleted);
        command.Parameters.AddWithValue("contacts", contactDetailsDeleted);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>A cycle that threw partway through: records how far this cycle got (its own share of
    /// messages, added to the running total) and why it stopped - <see cref="ErasureRecordEntity"/>'s
    /// own remarks on why <paramref name="failureReasonType"/> is a exception <em>type</em> name, never
    /// its message. <c>status</c> moves to <c>'Failed'</c> from whatever it was
    /// (<c>ErasureRecordStatus</c>'s own remarks on why that is not a terminal state here) - the next
    /// cycle that finishes the conversation calls <see cref="CompleteConversationErasureAsync"/>, which
    /// overwrites both <c>status</c> and <c>failure_reason</c> unconditionally.</summary>
    public static async Task FailConversationErasureAsync(
        NpgsqlConnection connection, Guid? recordId, int messagesDeletedThisCycle, string failureReasonType,
        DateTimeOffset failedAt, CancellationToken cancellationToken)
    {
        if (recordId is null)
        {
            return;
        }

        await using var command = new NpgsqlCommand(
            """
            update erasure_records
            set status = 'Failed',
                completed_at = @failedAt,
                failure_reason = @reason,
                messages_deleted = messages_deleted + @messagesDelta
            where id = @id and status <> 'Completed'
            """,
            connection);
        command.Parameters.AddWithValue("id", recordId.Value);
        command.Parameters.AddWithValue("failedAt", failedAt);
        command.Parameters.AddWithValue("reason", failureReasonType);
        command.Parameters.AddWithValue("messagesDelta", messagesDeletedThisCycle);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // ---------------------------------------------------------------------------------------------
    // Site scope
    // ---------------------------------------------------------------------------------------------

    /// <summary>Adds this tick's own newly-stamped-conversation count to the running total - called
    /// every tick <c>SiteErasureQuery.StampConversationsAsync</c> stamps at least one new conversation,
    /// whether or not this tick also finishes the site. By the time the site's own record reaches
    /// <c>Completed</c>, every conversation this column ever counted has necessarily been fully drained
    /// by <see cref="ConversationErasureJob"/> - <c>SiteErasureJob.ProcessSiteAsync</c>'s own
    /// <c>HasAnyConversationAsync</c> gate cannot pass otherwise - so the total is an honest count of
    /// conversations erased, not merely scheduled, once the site's own status says so; the column is
    /// named <c>conversations_marked_for_erasure</c> rather than "erased" so it reads correctly even
    /// mid-flight, before that guarantee holds.</summary>
    public static async Task AddConversationsMarkedAsync(
        NpgsqlConnection connection, Guid? recordId, int newlyMarked, CancellationToken cancellationToken)
    {
        if (recordId is null || newlyMarked == 0)
        {
            return;
        }

        await using var command = new NpgsqlCommand(
            "update erasure_records set conversations_marked_for_erasure = conversations_marked_for_erasure + @delta "
            + "where id = @id and status <> 'Completed'",
            connection);
        command.Parameters.AddWithValue("id", recordId.Value);
        command.Parameters.AddWithValue("delta", newlyMarked);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The finishing call for a site erasure - both counts are set outright because
    /// <c>SiteErasureJob.ProcessSiteAsync</c> only ever calls this once, immediately before it reads
    /// and deletes the site row (this file's own remarks on why it runs there and not after: the
    /// identity-provider deletions that follow are already the one step this job's own comments accept
    /// as unretryable once the site row is gone, and this record should not be the second one).</summary>
    public static async Task CompleteSiteErasureAsync(
        NpgsqlConnection connection, Guid? recordId, int storageObjectsDeleted, int identitiesDeleted,
        DateTimeOffset completedAt, CancellationToken cancellationToken)
    {
        if (recordId is null)
        {
            return;
        }

        await using var command = new NpgsqlCommand(
            """
            update erasure_records
            set status = 'Completed',
                completed_at = @completedAt,
                failure_reason = null,
                storage_objects_deleted = @objects,
                identities_deleted = @identities
            where id = @id and status <> 'Completed'
            """,
            connection);
        command.Parameters.AddWithValue("id", recordId.Value);
        command.Parameters.AddWithValue("completedAt", completedAt);
        command.Parameters.AddWithValue("objects", storageObjectsDeleted);
        command.Parameters.AddWithValue("identities", identitiesDeleted);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The site-scope sibling of <see cref="FailConversationErasureAsync"/> - same reasoning,
    /// no per-cycle count to add (a site's own steps, unlike messages, are never partially attempted
    /// across cycles before the point where <see cref="CompleteSiteErasureAsync"/> would run).</summary>
    public static async Task FailSiteErasureAsync(
        NpgsqlConnection connection, Guid? recordId, string failureReasonType, DateTimeOffset failedAt,
        CancellationToken cancellationToken)
    {
        if (recordId is null)
        {
            return;
        }

        await using var command = new NpgsqlCommand(
            """
            update erasure_records
            set status = 'Failed', completed_at = @failedAt, failure_reason = @reason
            where id = @id and status <> 'Completed'
            """,
            connection);
        command.Parameters.AddWithValue("id", recordId.Value);
        command.Parameters.AddWithValue("failedAt", failedAt);
        command.Parameters.AddWithValue("reason", failureReasonType);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
