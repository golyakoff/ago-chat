using Ago.Chat.Domain;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `19-02`: the candidate scan for `ConversationCategorizationJob` - a plain, unlocked `SELECT`, the
/// same "nothing races here, the real check happens where the write happens" shape
/// `AutoCloseInactiveConversationsQuery`'s own remarks describe. The actual "is this still eligible"
/// check happens inside `CategorizeConversationHandler` (still untagged, this site still has a
/// vocabulary) at the moment it runs, not here - a stale candidate this query named costs one wasted
/// `CategorizationOutcome.AlreadyTagged`, never a corrupted write.
///
/// <para><b>Both predicates matter for what "worth classifying" means here.</b> `state = 'Closed'`:
/// `adr/0078`'s kind 2 names "run asynchronously after a conversation closes" as the correct shape - a
/// closed conversation's transcript is its whole, final shape, unlike an open one a categorizer would
/// see only partially. `closed_at >= @cutoff`: bounds the scan to conversations closed within
/// `ConversationCategorizationJobOptions.LookbackWindow`, which is also what keeps a site with an empty
/// tag vocabulary (`CategorizeConversationHandler`'s own `NoTagsConfigured` no-op) from being rescanned
/// forever - once a conversation's own `closed_at` ages out of the window, this query simply stops
/// naming it, with no separate "already tried, skip" bookkeeping needed.</para>
///
/// <para><b>The `NOT EXISTS` predicate is covered by the existing primary key.</b>
/// `conversation_tags`'s own composite primary key is `(conversation_id, tag_id)`
/// (`ConversationTagRecordConfiguration`), so this subquery's own `ct.conversation_id = c.id` filter
/// already has an index with `conversation_id` as its leading column - no new index needed for this
/// item, unlike `AutoCloseInactiveConversationsQuery`'s own `channel_identities.visitor_id` gap.</para>
///
/// <para><b>No dedicated index for `conversations.state = 'Closed'` or `closed_at`</b>, the identical
/// "shares an existing gap, not worth inventing a migration ahead of real evidence" position
/// `AutoCloseInactiveConversationsQuery`'s own remarks take for `state = 'Assigned'`.</para>
/// </summary>
public static class ConversationCategorizationQuery
{
    private const string Sql = """
        SELECT c.id, c.site_id
        FROM conversations c
        WHERE c.state = 'Closed'
          AND c.closed_at >= @cutoff
          AND NOT EXISTS (SELECT 1 FROM conversation_tags ct WHERE ct.conversation_id = c.id)
        ORDER BY c.closed_at
        LIMIT @batchSize
        """;

    /// <param name="cutoff">Only conversations closed at or after this instant are candidates -
    /// <see cref="ConversationCategorizationJobOptions.LookbackWindow"/>'s own boundary.</param>
    public static async Task<IReadOnlyList<(ConversationId ConversationId, SiteId SiteId)>> FindUncategorizedClosedBatchAsync(
        NpgsqlConnection connection, DateTimeOffset cutoff, int batchSize, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(Sql, connection);
        command.Parameters.AddWithValue("cutoff", cutoff);
        command.Parameters.AddWithValue("batchSize", batchSize);

        var candidates = new List<(ConversationId, SiteId)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add((new ConversationId(reader.GetGuid(0)), new SiteId(reader.GetGuid(1))));
        }

        return candidates;
    }
}
