using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `adr/0189`/`26-275` slice #3: the SQL half of <see cref="PersonErasureJob"/> - raw Npgsql, the
/// identical "one file, every method is one step of the same stamp-and-wait sequence" shape
/// <see cref="SiteErasureQuery"/> already establishes for its own whole-site version of the same idea,
/// applied one aggregate down: a person's conversations, not a site's.
/// </summary>
/// <param name="PersonId">The visitor id - `adr/0184`'s own person, elevated from `Visitor`.</param>
public sealed record PendingPersonErasure(Guid PersonId);

public static class PersonErasureQuery
{
    public static async Task<IReadOnlyList<PendingPersonErasure>> ListPendingAsync(
        NpgsqlConnection connection, int limit, CancellationToken cancellationToken)
    {
        const string sql = """
            select id
            from visitors
            where erasure_requested_at is not null
            order by erasure_requested_at
            limit @limit
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("limit", limit);

        var pending = new List<PendingPersonErasure>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            pending.Add(new PendingPersonErasure(reader.GetGuid(0)));
        }

        return pending;
    }

    /// <summary>Idempotently stamps every conversation belonging to this person that does not already
    /// carry the flag - the identical shape <see cref="SiteErasureQuery.StampConversationsAsync"/>
    /// already establishes for a whole site, scoped to <c>visitor_id</c> instead of <c>site_id</c>. Safe
    /// to repeat every tick, including for a conversation started *after* this person's own erasure was
    /// requested - impossible in practice once a real client-deletion flow is in place (the calendar's own
    /// future-bookings guard, `adr/0189`, and nothing prevents a *chat-only* conversation starting mid-
    /// erasure today), but self-healing regardless, the same reasoning <see cref="SiteErasureQuery"/>'s
    /// own remarks give for the identical case.
    ///
    /// <para>Deliberately does **not** set `erasure_requested_by`/`erasure_record_id` - each conversation
    /// stamped this way is proof of the *person's* own erasure, not a standalone request of its own, the
    /// identical "no receipt of its own" reasoning <see cref="SiteErasureQuery.StampConversationsAsync"/>'s
    /// own remarks give for a site-wide sweep.</para>
    /// </summary>
    public static async Task<int> StampConversationsAsync(
        NpgsqlConnection connection, Guid personId, DateTimeOffset requestedAt, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "update conversations set erasure_requested_at = @requestedAt where visitor_id = @personId and erasure_requested_at is null",
            connection);
        command.Parameters.AddWithValue("requestedAt", requestedAt);
        command.Parameters.AddWithValue("personId", personId);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Whether any conversation still exists for this person - the identical
    /// <see cref="SiteErasureQuery.HasAnyConversationAsync"/> shape, scoped one aggregate down. This bound
    /// is what keeps the eventual <see cref="DeleteVisitorAsync"/> from relying on `conversations`'
    /// required foreign key to `visitors` to do the bulk removal: that FK defaults to
    /// <c>ON DELETE CASCADE</c> (a required column, EF's own convention), which would otherwise let one
    /// unbounded cascading `DELETE` remove every conversation - and, through it, every message and
    /// attachment - this person ever had, exactly the anti-pattern `ConversationErasureJob`'s own bounded,
    /// ordered removal exists to avoid. Waiting for this to report empty is what makes the eventual
    /// <c>DELETE FROM visitors</c> a true no-op on that cascade, the same "cascading here deletes zero
    /// rows rather than being the mechanism that empties them" posture
    /// <see cref="SiteErasureQuery.DeleteSiteAsync"/>'s own remarks state for the identical
    /// concern.</summary>
    public static async Task<bool> HasAnyConversationAsync(
        NpgsqlConnection connection, Guid personId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select exists(select 1 from conversations where visitor_id = @personId)", connection);
        command.Parameters.AddWithValue("personId", personId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>
    /// The visitor row itself - one statement, once <see cref="HasAnyConversationAsync"/> has confirmed
    /// no conversation of theirs remains. Relies on the schema's own cascades for what this person still
    /// owns at that point - `visitor_contact_details`, `person_notes` and `channel_identities` (each a
    /// required FK to `visitors`, `ON DELETE CASCADE` - <c>VisitorContactDetailConfiguration</c>/
    /// <c>PersonNoteConfiguration</c>/<c>ChannelIdentityConfiguration</c>'s own remarks), and
    /// `visitor_restrictions` (`Stage23AddVisitorRestrictions`'s own migration, the identical cascade
    /// <see cref="SiteErasureQuery.DeleteVisitorRestrictionsForSiteAsync"/>'s own remarks describe for the
    /// site-wide case). Every one of those is genuinely a no-cost cascade here, unlike
    /// `conversations`/`messages`/`attachments`: by the time this runs, every conversation of this
    /// person's own has already had its own contact details and person notes explicitly drained by
    /// <see cref="ConversationErasureJob.EraseConversationAsync"/>'s own visitor-wide steps
    /// (<c>DeleteContactDetailsForVisitorAsync</c>/<c>DeletePersonNotesForVisitorAsync</c>) - the same
    /// "primary mechanism is explicit, cascade is defence in depth" shape those two calls already are for
    /// a single conversation's own erasure. A person with *no* conversation at all (a manual client,
    /// `26-268`, who never opened a chat) never went through that drain, which is exactly the case this
    /// cascade is not merely a backstop for: the schema-level `ON DELETE CASCADE` is the *only* mechanism
    /// that removes such a person's contact details and notes, and it is correct to rely on it there
    /// because there is no unbounded table underneath (a person has at most a handful of contact details
    /// and notes, never thousands of messages).
    /// </summary>
    public static async Task<int> DeleteVisitorAsync(
        NpgsqlConnection connection, Guid personId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("delete from visitors where id = @personId", connection);
        command.Parameters.AddWithValue("personId", personId);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
