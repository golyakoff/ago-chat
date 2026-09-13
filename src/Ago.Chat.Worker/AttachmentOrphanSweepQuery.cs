using Ago.Chat.Domain;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `5-04`: an atomic compare-and-delete, not a load-then-decide - one `DELETE ... WHERE state =
/// 'Pending' ... RETURNING` statement, following <see cref="WaitingConversationClaimQuery"/>'s own
/// raw-Npgsql-in-the-Worker shape for exactly this kind of claim. This single statement *is* the
/// ordering guarantee `5-04`'s Done-when asks for: Postgres evaluates the inner `WHERE state =
/// 'Pending'` against the row's committed state at the moment this statement executes, so an
/// attachment confirmed (state flipped to `Ready`) by a transaction that commits before this one runs
/// is already excluded - there is no separate "select candidates" step for a race to land inside.
/// `FOR UPDATE SKIP LOCKED` in the inner subquery additionally means a row a concurrent confirm is
/// still in the middle of updating (locked, not yet committed) is skipped outright rather than making
/// this statement wait on it - proven directly in <c>AttachmentOrphanSweepJobTests</c>.
///
/// <para><b>`23-75`: the same statement also releases each swept row's own conversation-budget
/// reservation</b> - a second CTE, <c>released</c>, folded into this identical atomic statement rather
/// than a separate <c>IConversationAttachmentBudget.ReleaseAsync</c> call issued afterward for every
/// claimed row. "Reserved when the slot is issued, released by the existing pending sweep" (`23-75`'s
/// own design point) only actually holds if the release cannot land without the delete, or the delete
/// without the release - two statements, even in a loop over the same connection with no intervening
/// commit, would still leave a window where a crash between them deletes the row but leaves its
/// reservation stuck forever, invisible to any future sweep (nothing ever looks at a row that no
/// longer exists). One statement closes that window by construction: Postgres either applies both CTEs
/// or neither, the same "this single statement is the ordering guarantee" property `ClaimExpiredPendingBatchAsync`
/// already relies on for the delete half alone. <c>released</c> groups by <c>conversation_id</c> and
/// sums <c>size_bytes</c> before applying one <c>UPDATE</c> per distinct conversation in the batch,
/// rather than one release per attachment row - a batch of orphans rarely belongs to one conversation
/// alone, but summing first means a conversation with several expired attachments in the same batch
/// gets exactly one row-lock acquisition, not one per orphan.</para>
///
/// <para><b>`23-76`: a third CTE, <c>siteReleased</c>, releases each swept row's own site-level
/// reservation in the identical statement</b> - the same "reserved by presign, released by the
/// existing sweep, not a second mechanism" design point the `released` CTE above states for
/// the conversation-level column, extended to <c>sites.attachment_bytes_reserved</c>
/// (<c>ISiteAttachmentStorageBudget</c>). An abandoned presigned upload reserved bytes against both
/// the conversation's own ceiling and the tenant's - releasing only the first would leak the second
/// forever, since a `Pending` row this sweep deletes is the only place either reservation is ever
/// tied back to a real attachment. Grouped by <c>site_id</c>, the identical "sum first, one row-lock
/// per distinct key in the batch" shape `released` already uses for <c>conversation_id</c>.</para>
/// </summary>
public static class AttachmentOrphanSweepQuery
{
    public static async Task<IReadOnlyList<(AttachmentId Id, string ObjectKey)>> ClaimExpiredPendingBatchAsync(
        NpgsqlConnection connection, DateTimeOffset olderThan, int batchSize, CancellationToken cancellationToken)
    {
        const string sql = """
            WITH claimed AS (
                DELETE FROM attachments
                WHERE id IN (
                    SELECT id
                    FROM attachments
                    WHERE state = 'Pending' AND created_at < @olderThan
                    ORDER BY created_at
                    LIMIT @batchSize
                    FOR UPDATE SKIP LOCKED
                )
                RETURNING id, object_key, conversation_id, site_id, size_bytes
            ),
            released AS (
                UPDATE conversations c
                SET attachment_bytes_reserved = GREATEST(c.attachment_bytes_reserved - agg.total, 0)
                FROM (
                    SELECT conversation_id, SUM(size_bytes) AS total
                    FROM claimed
                    GROUP BY conversation_id
                ) agg
                WHERE c.id = agg.conversation_id
                RETURNING c.id
            ),
            siteReleased AS (
                UPDATE sites s
                SET attachment_bytes_reserved = GREATEST(s.attachment_bytes_reserved - agg.total, 0)
                FROM (
                    SELECT site_id, SUM(size_bytes) AS total
                    FROM claimed
                    GROUP BY site_id
                ) agg
                WHERE s.id = agg.site_id
                RETURNING s.id
            )
            SELECT id, object_key FROM claimed
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("olderThan", olderThan);
        command.Parameters.AddWithValue("batchSize", batchSize);

        var claimed = new List<(AttachmentId, string)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            claimed.Add((new AttachmentId(reader.GetGuid(0)), reader.GetString(1)));
        }

        return claimed;
    }
}
