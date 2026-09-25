using Ago.Chat.Domain;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `4-01`: the waiting-queue claim `4-02`'s assignment engine will loop over -
/// <c>SELECT ... FOR UPDATE SKIP LOCKED</c>, raw Npgsql in the Worker host, following
/// <see cref="OutboxDispatcher"/>'s own established shape for exactly this SQL pattern
/// (<see cref="ClaimedOutboxRow"/>). Deliberately not a Dapper read behind
/// <c>IConversationReadStore</c>: a <c>SKIP LOCKED</c> claim only makes sense inside the same
/// transaction the caller commits or rolls back - the row lock it takes is released the instant
/// that transaction ends, whether or not the caller actually assigned anything, which is exactly
/// what lets a claimed-but-unassignable conversation go back to being visible on the very next
/// tick without any explicit "unclaim" step.
///
/// No caller yet - `4-02`'s assignment loop is the real one. Tested standalone here (real
/// concurrent transactions, not sequential awaits) because the guarantee this exists to prove -
/// two transactions racing the same site's queue split the rows, never double-claim one - does not
/// need a caller to be true or to be checked.
/// </summary>
public static class WaitingConversationClaimQuery
{
    public static async Task<IReadOnlyList<ConversationId>> ClaimBatchAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, SiteId siteId, int batchSize, CancellationToken cancellationToken)
    {
        // `24-10`: `blocked_at IS NULL` - a blocked conversation must not be routed to an operator by
        // the automatic assignment engine (this item's own decided reading of its open question: "an
        // inbound message from a blocked visitor is... not routed"). A `Waiting` conversation can be
        // blocked the same as any other - nothing about this item restricts blocking to an already-
        // assigned or closed conversation.
        //
        // `23-69`/`23-77`: `routing_suppressed_at IS NULL` joins it - a conversation created for a
        // visitor who already carried an active restriction is stamped with this at the moment
        // `Conversation.Start` created it (`Conversation.RoutingSuppressedAt`'s own remarks) and must
        // never be claimed here, the identical "never routed to an operator" guarantee `blocked_at`
        // already gives, restated for a second, deliberately separate flag.
        //
        // `26-119`: the trailing correlated subquery is this item's own fix for `26-83`'s churn - a
        // `Waiting` conversation is claimable only when its own most recent message (highest
        // `sequence`, never `created_at`: sequence is the server-assigned order `CLAUDE.md` rule 11
        // requires, `created_at` is merely when it happened) was authored by the visitor.
        // `AutoCloseInactiveConversationsJob.ReleaseStaleAssignedWidgetBatchAsync` moves an `Assigned`
        // conversation back to `Waiting` purely because *neither side* has written anything recently -
        // it never inspects who wrote last. Two shapes reach `Waiting` through that release, and only
        // one of them is genuinely owed an operator: the operator already answered (or `14-04`'s
        // offline auto-reply did, `MessageAuthorKind.System`) and the visitor simply went quiet - the
        // latest message's author is `Operator`/`System`, nothing is pending, and re-assigning it every
        // release cycle is exactly the push-notification churn `26-83` measured live. Or the visitor
        // wrote and nobody answered before the inactivity window elapsed - the latest message's author
        // is still `Visitor`, an operator genuinely owes this conversation a reply, and it must be
        // assigned exactly as before. A conversation that reached `Waiting` any other way (a fresh
        // `ConversationEnteredQueue`, `4-04`'s disconnect release) always has the visitor's own message
        // as its latest by construction (`Conversation.AddOperatorMessage`/`AddSystemMessage` both
        // require `Assigned`, so neither can ever be the newest message on a `Waiting` row unless a
        // release like this one put it there) - so this predicate changes nothing for the ordinary
        // queue, only for the idle-released case it exists to catch. `m.site_id = c.site_id` joins the
        // correlated subquery on the partition key first, matching
        // `AutoCloseInactiveConversationsQuery`'s own reasoning for why that keeps `messages`
        // (`PARTITION BY HASH (site_id)`, `15-09`/`adr/0087`) pruned to one bucket per candidate row
        // rather than scanned whole. Every `Waiting` conversation has at least one message by
        // construction (`Conversation.Start` leaves it `Pending`; only `AddVisitorMessage`'s own
        // Pending -> Waiting transition ever produces a `Waiting` row, and it always runs after
        // inserting that very message) - the subquery cannot return no rows for a real candidate, so
        // there is no `NULL`-comparison edge case to reason about here.
        //
        // `26-138`: the second, trailing predicate is this item's own extension of that same fix. `26-119`
        // above catches the idle-released conversation whose *operator* replied last; it does not catch the
        // one whose latest message is still the visitor's - an unanswered conversation that went quiet
        // before anyone replied and was then released for inactivity. That row satisfies the `26-119`
        // predicate (the visitor did write last), so before this item it was re-claimed - and re-pushed -
        // every release cycle, which is the remaining half of `26-83`'s churn. `Conversation.ReleaseToQueue`
        // now stamps `released_waiting_at_sequence` with the conversation's `last_sequence` at the moment the
        // inactivity job releases it (and only then - `4-04`'s disconnect release deliberately leaves it
        // null, because that path *does* want immediate re-routing). The predicate below therefore claims a
        // released conversation only once a visitor message *newer than that release point* exists: the
        // latest message's `sequence` (the same top-1 row `26-119` already inspects, guaranteed to be the
        // visitor's by the predicate right above) must exceed the marker. A released-and-untouched
        // conversation has `last_sequence == released_waiting_at_sequence`, so `> ` is false and it is never
        // re-claimed; a new post-release visitor message bumps `last_sequence` past the marker and it is
        // claimed and assigned exactly as before. The whole predicate is skipped when the marker is null
        // (never released for inactivity - the ordinary queue, a fresh `ConversationEnteredQueue`, or a
        // `4-04` disconnect release), so nothing but the idle-released case it exists to catch is affected.
        // The second correlated subquery runs only for the marker-set rows the `OR` short-circuits into, and
        // is the identical top-1-by-sequence scan `26-119`'s own subquery already performs (same
        // `m.site_id = c.site_id` partition-key prune) - not a new access shape.
        const string sql = """
            SELECT c.id
            FROM conversations c
            WHERE c.site_id = @siteId AND c.state = 'Waiting' AND c.blocked_at IS NULL AND c.routing_suppressed_at IS NULL
              AND (
                  SELECT m.author_kind
                  FROM messages m
                  WHERE m.conversation_id = c.id AND m.site_id = c.site_id
                  ORDER BY m.sequence DESC
                  LIMIT 1
              ) = 'Visitor'
              AND (
                  c.released_waiting_at_sequence IS NULL
                  OR (
                      SELECT m.sequence
                      FROM messages m
                      WHERE m.conversation_id = c.id AND m.site_id = c.site_id
                      ORDER BY m.sequence DESC
                      LIMIT 1
                  ) > c.released_waiting_at_sequence
              )
            ORDER BY c.created_at
            LIMIT @batchSize
            FOR UPDATE SKIP LOCKED
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("siteId", siteId.Value);
        command.Parameters.AddWithValue("batchSize", batchSize);

        var claimed = new List<ConversationId>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            claimed.Add(new ConversationId(reader.GetGuid(0)));
        }

        return claimed;
    }
}
