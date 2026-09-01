using Ago.Chat.Domain;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `18-06`: the candidate scan for `AutoCloseInactiveConversationsJob` - a plain, unlocked `SELECT`,
/// unlike `AttachmentOrphanSweepQuery`'s atomic `DELETE ... RETURNING`. There is nothing to race here:
/// the actual state transition happens through <c>Conversation.Close()</c> and
/// <c>IConversationRepository.SaveAsync</c>'s own optimistic-concurrency check (`6-08`), which is what
/// really decides whether a candidate this query names is still closable by the time
/// `AutoCloseConversationHandler` gets to it. A stale candidate - one an operator's own close, a new
/// message, or `4-04`'s disconnect release already moved on from - costs one wasted `InvalidState` (or
/// a reload-and-retry on a genuine `xmin` conflict), logged and left for next cycle
/// (`AutoCloseInactiveConversationsJob`'s own remarks), never a corrupted close.
///
/// <para><b>Both predicates are bounded by <paramref name="cutoff"/>, deliberately - though what
/// `m.created_at >= @cutoff` buys changed under `15-09`/`adr/0087`.</b> Before this item, `messages` was
/// `PARTITION BY RANGE (created_at)`, so this predicate was what let Postgres prune to the partitions
/// the window actually covered. `messages` is now `PARTITION BY HASH (site_id)` - `created_at` carries
/// no pruning power any more, and it is `m.site_id = c.site_id` (this class's own remarks just below)
/// that prunes each per-row subquery execution to one bucket instead. `m.created_at >= @cutoff` is still
/// worth keeping regardless: it is what makes this a bounded, recent-window scan within that one already-
/// pruned bucket rather than a scan of the conversation's entire history, the same ordinary index-scan
/// cost reasoning `12-02`'s own 30-day read bounds itself with. `c.created_at < @cutoff` is not just the
/// zero-messages fallback (a conversation assigned but never messaged, however rare) - it is also what
/// keeps a conversation created seconds ago, with no messages yet, from ever being a false positive:
/// without it, "no message at or after cutoff" would be trivially true for a conversation that simply
/// has not had time to receive one.</para>
///
/// <para><b>No dedicated index for `conversations.state = 'Assigned'`.</b>
/// `OperatorDisconnectSweepJob`'s own candidate query (`4-04`) already scans on the same predicate with
/// none, at the same "every replica, every tick" cadence - this query does not introduce a new gap, it
/// shares an existing one. Worth an index if either job's cadence or this deployment's conversation
/// volume ever makes it show up in `pg_stat_statements`; not worth a migration invented ahead of that
/// evidence (CLAUDE.md's "measure, don't invent" rule cuts both ways).</para>
///
/// <para><b>No index on `channel_identities.visitor_id` either</b>, and this one is a genuine, new gap:
/// nothing before this item ever looked up a channel identity by visitor rather than by
/// (site, kind, address) (`IChannelIdentityRepository`'s own shape). At this project's scale the
/// sequential scan this correlated subquery runs per candidate row is not worth a migration on its own
/// say-so either - flagged in this item's own report rather than added here, both to avoid a second
/// migration landing in the same wave as `13-01`'s (this repository's own background-worker-brief
/// convention) and because "arrives with its first real reader" (`ConversationConfiguration`'s own
/// words, for a different column) is exactly the position to add an index from, not skip past.</para>
/// </summary>
public static class AutoCloseInactiveConversationsQuery
{
    // `15-09`/`adr/0087`: each `NOT EXISTS` subquery's own `m.site_id = c.site_id` needs no new bind
    // parameter or query-level site scope - this whole sweep is deliberately cross-tenant (candidates
    // come from every site at once, this class's own remarks explain why there is no single site_id to
    // filter on up front), but `c.site_id` is already selected on the correlated outer row, so each
    // per-row execution of the subquery still prunes to exactly one of the 64 messages buckets instead
    // of touching all of them for every candidate conversation checked.
    private const string WidgetSql = """
        SELECT c.id
        FROM conversations c
        WHERE c.state = 'Assigned'
          AND c.created_at < @cutoff
          AND NOT EXISTS (SELECT 1 FROM channel_identities ci WHERE ci.visitor_id = c.visitor_id)
          AND NOT EXISTS (
              SELECT 1 FROM messages m
              WHERE m.conversation_id = c.id AND m.site_id = c.site_id AND m.created_at >= @cutoff
          )
        ORDER BY c.created_at
        LIMIT @batchSize
        """;

    private const string ChannelSql = """
        SELECT c.id
        FROM conversations c
        WHERE c.state = 'Assigned'
          AND c.created_at < @cutoff
          AND EXISTS (
              SELECT 1 FROM channel_identities ci
              WHERE ci.visitor_id = c.visitor_id AND ci.kind = @kind
          )
          AND NOT EXISTS (
              SELECT 1 FROM messages m
              WHERE m.conversation_id = c.id AND m.site_id = c.site_id AND m.created_at >= @cutoff
          )
        ORDER BY c.created_at
        LIMIT @batchSize
        """;

    /// <param name="channelKind"><see langword="null"/> for widget conversations (no
    /// `channel_identities` row for their visitor - `ChannelKind`'s own remarks on why the widget is
    /// not a member of that enum); otherwise scans conversations linked to a visitor with a
    /// `channel_identities` row of exactly this kind.</param>
    /// <param name="cutoff">Conversations with no message (either direction) at or after this instant,
    /// created before it, are candidates.</param>
    public static async Task<IReadOnlyList<ConversationId>> FindStaleAssignedBatchAsync(
        NpgsqlConnection connection, ChannelKind? channelKind, DateTimeOffset cutoff, int batchSize,
        CancellationToken cancellationToken)
    {
        var sql = channelKind is null ? WidgetSql : ChannelSql;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("cutoff", cutoff);
        command.Parameters.AddWithValue("batchSize", batchSize);
        if (channelKind is { } kind)
        {
            // Stored (and compared) as the CLR member name - ChannelIdentityConfiguration's own
            // default HasConversion<string>() mapping, so `kind.ToString()` is exactly what is on the
            // row.
            command.Parameters.AddWithValue("kind", kind.ToString());
        }

        var ids = new List<ConversationId>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(new ConversationId(reader.GetGuid(0)));
        }

        return ids;
    }
}
