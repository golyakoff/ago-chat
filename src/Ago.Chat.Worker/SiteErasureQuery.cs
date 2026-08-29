using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `16-02`: the SQL half of <see cref="SiteErasureJob"/> - raw Npgsql, the same shape
/// <see cref="ConversationErasureQuery"/> and `Ago.Chat.Infrastructure.Postgres`'s own
/// `DemoTenantRepository` already establish for exactly this kind of cross-aggregate removal.
/// </summary>
public static class SiteErasureQuery
{
    public static async Task<IReadOnlyList<Guid>> ListPendingAsync(
        NpgsqlConnection connection, int limit, CancellationToken cancellationToken)
    {
        const string sql = """
            select id
            from sites
            where erasure_requested_at is not null
            order by erasure_requested_at
            limit @limit
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("limit", limit);

        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    /// <summary>Idempotently stamps every conversation belonging to this site that does not already
    /// carry the flag - cheap (an indexed `UPDATE ... WHERE site_id = @siteId AND erasure_requested_at
    /// is null`) and safe to repeat every tick, including for a conversation created *after* the site's
    /// own erasure was requested: this call re-finds it on the very next tick, which is what makes a
    /// visitor starting a new conversation mid-erasure self-healing rather than a race the site erasure
    /// can complete around.</summary>
    public static async Task<int> StampConversationsAsync(
        NpgsqlConnection connection, Guid siteId, DateTimeOffset requestedAt, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "update conversations set erasure_requested_at = @requestedAt where site_id = @siteId and erasure_requested_at is null",
            connection);
        command.Parameters.AddWithValue("requestedAt", requestedAt);
        command.Parameters.AddWithValue("siteId", siteId);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Whether any conversation still exists for this site - regardless of its own erasure
    /// flag, because a site row must never be deleted while any conversation (and, through it, any
    /// message or attachment) still exists; that bound is what keeps this site's own removal from ever
    /// becoming the one unbounded cascading `DELETE` this item's bounded-batch design exists to
    /// avoid.</summary>
    public static async Task<bool> HasAnyConversationAsync(
        NpgsqlConnection connection, Guid siteId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select exists(select 1 from conversations where site_id = @siteId)", connection);
        command.Parameters.AddWithValue("siteId", siteId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>Every operator's Keycloak subject id for this site, read before any row is deleted -
    /// the same "read the subject ids before the delete removes the rows holding them" ordering
    /// `DemoTenantRepository.ListExpiredAsync`'s own remarks give.</summary>
    public static async Task<IReadOnlyList<string>> ListOperatorSubjectIdsAsync(
        NpgsqlConnection connection, Guid siteId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select external_subject_id from operators where site_id = @siteId and external_subject_id is not null",
            connection);
        command.Parameters.AddWithValue("siteId", siteId);

        var subjectIds = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            subjectIds.Add(reader.GetString(0));
        }

        return subjectIds;
    }

    /// <summary>The site's own `public_key`, read before deletion - <c>SiteErasureJob</c> needs it to
    /// invalidate <c>SiteCacheKeys.ForPublicKey</c> after the row is gone, since the key cannot be
    /// reconstructed once the row that names it is deleted. Reading it ahead of the delete carries none
    /// of the object-store-first hazard <see cref="ConversationErasureQuery.ListAttachmentObjectKeysAsync"/>
    /// exists to avoid - a cache-invalidation broadcast never orphans anything the way an unreachable
    /// MinIO object would; it is a pure "which key to evict" lookup.</summary>
    public static async Task<string?> GetPublicKeyAsync(
        NpgsqlConnection connection, Guid siteId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select public_key from sites where id = @siteId", connection);
        command.Parameters.AddWithValue("siteId", siteId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result as string;
    }

    /// <summary>
    /// The site row itself - one statement, relying on the schema's cascades for everything still
    /// attached to it, the identical "one line, not a hand-ordered list of deletes" reasoning
    /// `DemoTenantRepository.DeleteSiteAsync`'s own remarks give in full: `operators` (and
    /// `operator_roles` through it), `roles` (and `operator_roles` through it too), `visitors`,
    /// `channel_identities`, `webhook_endpoints` (and `webhook_deliveries` through it), and - `18-04`
    /// - `tags` (`TagConfiguration`'s own required FK, `ON DELETE CASCADE`) - every one a required
    /// foreign key to `sites`. By the time this runs, `conversations`/`messages`/`attachments` are
    /// already empty for this site (<see cref="HasAnyConversationAsync"/> gates it), and so are
    /// `conversation_notes`/`conversation_tags` (drained per-conversation by
    /// <see cref="ConversationErasureQuery.DeleteNotesForConversationAsync"/>/
    /// <see cref="ConversationErasureQuery.DeleteTagsForConversationAsync"/> before each conversation
    /// row itself was deleted) - so cascading into any of those five tables here deletes zero rows
    /// rather than being the mechanism that empties them - the bounded <see cref="ConversationErasureQuery"/>
    /// is what actually did that work, batch by batch. `tags` is the one table in this cascade list
    /// that genuinely still has rows at this point: the tag *vocabulary* itself, which nothing drains
    /// per-conversation because a tag definition outlives any one conversation that carried it.
    /// </summary>
    public static async Task<int> DeleteSiteAsync(
        NpgsqlConnection connection, Guid siteId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("delete from sites where id = @siteId", connection);
        command.Parameters.AddWithValue("siteId", siteId);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
