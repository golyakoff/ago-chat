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
/// <para><b>No dedicated index for `conversations.state IN ('Assigned', 'Waiting')`.</b>
/// `OperatorDisconnectSweepJob`'s own candidate query (`4-04`) already scans on the same kind of
/// predicate with none, at the same "every replica, every tick" cadence - this query does not introduce
/// a new gap, it shares an existing one, `26-232`'s own widening of <see cref="ChannelSql"/> included.
/// Worth an index if either job's cadence or this deployment's conversation volume ever makes it show up
/// in `pg_stat_statements`; not worth a migration invented ahead of that evidence (CLAUDE.md's "measure,
/// don't invent" rule cuts both ways).</para>
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

    /// <summary>`25-118`: the widget-only "close" pass's own candidate scan - identical to
    /// <see cref="WidgetSql"/> in every predicate except `c.state`, which this widens from `= 'Assigned'`
    /// to `IN ('Assigned', 'Waiting')`. This is the one line that makes the design decision real: a
    /// `Waiting` widget conversation was never a candidate for anything before this item (`WidgetSql`
    /// structurally cannot select one), so widening it here is what lets a genuinely abandoned
    /// conversation that has already been released (see <see cref="FindStaleAssignedWidgetBatchAsync"/>'s
    /// own caller in `AutoCloseInactiveConversationsJob`) eventually actually close, once
    /// `AutoCloseInactiveConversationsJobOptions.WidgetCloseWindow` has also elapsed. No new bind
    /// parameter for the two literal state values, matching `WidgetSql`/`ChannelSql`'s own choice not to
    /// parameterise `'Assigned'` either - both are fixed by the shape of this SQL text, not caller input.
    /// </summary>
    private const string WidgetCloseSql = """
        SELECT c.id
        FROM conversations c
        WHERE c.state IN ('Assigned', 'Waiting')
          AND c.created_at < @cutoff
          AND NOT EXISTS (SELECT 1 FROM channel_identities ci WHERE ci.visitor_id = c.visitor_id)
          AND NOT EXISTS (
              SELECT 1 FROM messages m
              WHERE m.conversation_id = c.id AND m.site_id = c.site_id AND m.created_at >= @cutoff
          )
        ORDER BY c.created_at
        LIMIT @batchSize
        """;

    /// <summary>`26-232`: widened from `c.state = 'Assigned'` to `c.state IN ('Assigned', 'Waiting')` -
    /// the one-line change that closes the gap this item exists to fix. Before this, a channel-kind
    /// conversation released to `Waiting` (an operator-disconnect release, `4-04`, or any future path
    /// that does the same) was never a candidate for anything again: this scan structurally could not
    /// select it (`Assigned`-only), and nothing else ever closed a `Waiting` channel conversation either
    /// - so it sat in `Waiting` ("Ожидают") forever, however old it got. Reusing
    /// <see cref="AutoCloseInactiveConversationsJobOptions.WindowFor"/> for the cutoff (unchanged by
    /// this item - still the per-`ChannelKind` override, else `DefaultChannelInactivityWindow`) is what
    /// keeps the promise that the 24-hour default lives in exactly one place: this query does not get its
    /// own window, it just gets to see two more states of the same window's candidates.</summary>
    private const string ChannelSql = """
        SELECT c.id
        FROM conversations c
        WHERE c.state IN ('Assigned', 'Waiting')
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

    /// <summary>The widget-only "release" pass's own candidate scan (`25-118`) - `Assigned`-only,
    /// unchanged by `26-232`: <see cref="AutoCloseInactiveConversationsJob.ReleaseStaleAssignedWidgetBatchAsync"/>
    /// is the only caller, and its whole point is to catch a conversation <em>before</em> it would ever
    /// reach `Waiting` on its own. No `channelKind` parameter - this only ever scans widget conversations
    /// (no `channel_identities` row for their visitor).</summary>
    /// <param name="cutoff">Conversations with no message (either direction) at or after this instant,
    /// created before it, are candidates.</param>
    public static async Task<IReadOnlyList<ConversationId>> FindStaleAssignedWidgetBatchAsync(
        NpgsqlConnection connection, DateTimeOffset cutoff, int batchSize, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(WidgetSql, connection);
        command.Parameters.AddWithValue("cutoff", cutoff);
        command.Parameters.AddWithValue("batchSize", batchSize);

        var ids = new List<ConversationId>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(new ConversationId(reader.GetGuid(0)));
        }

        return ids;
    }

    /// <summary>`26-232`: the channel-kind close pass's own candidate scan - <see cref="ChannelSql"/>'s
    /// own remarks explain the one predicate this item changes and why. Named to match
    /// <see cref="FindStaleWidgetBatchIncludingWaitingAsync"/>'s own "reaches Waiting too" naming, now
    /// that both close passes share the same Assigned-or-Waiting shape; only the window and the extra
    /// `channel_identities` scope differ between them.</summary>
    /// <param name="channelKind">Scans conversations linked to a visitor with a `channel_identities`
    /// row of exactly this kind.</param>
    /// <param name="cutoff">Conversations with no message (either direction) at or after this instant,
    /// created before it, are candidates.</param>
    public static async Task<IReadOnlyList<ConversationId>> FindStaleChannelBatchIncludingWaitingAsync(
        NpgsqlConnection connection, ChannelKind channelKind, DateTimeOffset cutoff, int batchSize,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(ChannelSql, connection);
        command.Parameters.AddWithValue("cutoff", cutoff);
        command.Parameters.AddWithValue("batchSize", batchSize);
        // Stored (and compared) as the CLR member name - ChannelIdentityConfiguration's own default
        // HasConversion<string>() mapping, so `kind.ToString()` is exactly what is on the row.
        command.Parameters.AddWithValue("kind", channelKind.ToString());

        var ids = new List<ConversationId>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(new ConversationId(reader.GetGuid(0)));
        }

        return ids;
    }

    /// <summary>`25-118`: the widget-only "close" pass - see <see cref="WidgetCloseSql"/> for the one
    /// predicate that differs from <see cref="FindStaleAssignedWidgetBatchAsync"/>'s scan. No
    /// `channelKind` parameter: this is never called for the channel-kind buckets, which get the
    /// identical Assigned-or-Waiting widening through their own
    /// <see cref="FindStaleChannelBatchIncludingWaitingAsync"/> instead (`26-232`) - two call sites
    /// rather than one shared method, matching the pre-existing widget/channel SQL split
    /// (<see cref="WidgetSql"/>/<see cref="ChannelSql"/>) instead of a nullable-`channelKind` branch that
    /// would otherwise have to mean two different state sets depending on whether it is null.</summary>
    /// <param name="cutoff">Conversations with no message (either direction) at or after this instant,
    /// created before it, are candidates - the same contract <see cref="FindStaleAssignedWidgetBatchAsync"/>
    /// documents, just against `AutoCloseInactiveConversationsJobOptions.WidgetCloseWindow` rather than
    /// `WidgetInactivityWindow`.</param>
    public static async Task<IReadOnlyList<ConversationId>> FindStaleWidgetBatchIncludingWaitingAsync(
        NpgsqlConnection connection, DateTimeOffset cutoff, int batchSize, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(WidgetCloseSql, connection);
        command.Parameters.AddWithValue("cutoff", cutoff);
        command.Parameters.AddWithValue("batchSize", batchSize);

        var ids = new List<ConversationId>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(new ConversationId(reader.GetGuid(0)));
        }

        return ids;
    }
}
