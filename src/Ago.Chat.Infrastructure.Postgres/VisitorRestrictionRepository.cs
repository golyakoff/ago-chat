using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `23-69`/`23-77`: raw Npgsql, not EF - <see cref="IVisitorRestrictionRepository"/>'s own remarks
/// explain why (no aggregate, no invariant beyond "one row per event", the same reasoning
/// <see cref="AccessRecordRepository"/> already gives for itself).
/// </summary>
public sealed class VisitorRestrictionRepository(NpgsqlDataSource dataSource) : IVisitorRestrictionRepository
{
    public async Task RestrictAsync(
        SiteId siteId,
        VisitorId visitorId,
        OperatorId restrictedBy,
        VisitorRestrictionKind kind,
        DateTimeOffset? expiresAt,
        ConversationId sourceConversationId,
        Guid recordId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            insert into visitor_restrictions
                (id, site_id, visitor_id, kind, restricted_at, restricted_by, expires_at, source_conversation_id)
            values
                (@id, @siteId, @visitorId, @kind, @now, @restrictedBy, @expiresAt, @sourceConversationId)
            """,
            connection);
        command.Parameters.AddWithValue("id", recordId);
        command.Parameters.AddWithValue("siteId", siteId.Value);
        command.Parameters.AddWithValue("visitorId", visitorId.Value);
        command.Parameters.AddWithValue("kind", kind.ToString());
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("restrictedBy", restrictedBy.Value);
        // Explicit NpgsqlDbType, the same reason AccessRecordRepository's own remarks give: Npgsql
        // cannot infer a type from a bare null the one time expiresAt actually is one (a Block-kind,
        // indefinite restriction).
        command.Parameters.Add(new NpgsqlParameter("expiresAt", NpgsqlDbType.TimestampTz)
        {
            Value = (object?)expiresAt ?? DBNull.Value,
        });
        command.Parameters.AddWithValue("sourceConversationId", sourceConversationId.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> LiftAsync(
        SiteId siteId, VisitorId visitorId, OperatorId liftedBy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            update visitor_restrictions
            set lifted_at = @now, lifted_by = @liftedBy
            where site_id = @siteId and visitor_id = @visitorId
              and lifted_at is null and (expires_at is null or expires_at > @now)
            returning id
            """,
            connection);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("liftedBy", liftedBy.Value);
        command.Parameters.AddWithValue("siteId", siteId.Value);
        command.Parameters.AddWithValue("visitorId", visitorId.Value);

        // `IVisitorRestrictionRepository`'s own remarks: ordinarily zero or one active row, but more
        // than one is possible (no uniqueness constraint) - every currently-active row is lifted by
        // this one statement, not just the first found, so `IsActiveAsync` can never see a stale
        // second row after a caller believes it lifted "the" restriction.
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var liftedAny = false;
        while (await reader.ReadAsync(cancellationToken))
        {
            liftedAny = true;
        }

        return liftedAny;
    }

    public async Task<bool> IsActiveAsync(
        SiteId siteId, VisitorId visitorId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select exists (
                select 1 from visitor_restrictions
                where site_id = @siteId and visitor_id = @visitorId
                  and lifted_at is null and (expires_at is null or expires_at > @now)
            )
            """,
            connection);
        command.Parameters.AddWithValue("siteId", siteId.Value);
        command.Parameters.AddWithValue("visitorId", visitorId.Value);
        command.Parameters.AddWithValue("now", now);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is true;
    }

    public async Task<VisitorRestrictionKind?> GetActiveKindAsync(
        SiteId siteId, VisitorId visitorId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select kind
            from visitor_restrictions
            where site_id = @siteId and visitor_id = @visitorId
              and lifted_at is null and (expires_at is null or expires_at > @now)
            -- `Block` first when both kinds happen to be active at once (IVisitorRestrictionRepository's
            -- own remarks on why: the caller holding the Admin-only permission that a Block-kind lift
            -- needs can always also lift a mute).
            order by (kind <> 'Block'), restricted_at desc
            limit 1
            """,
            connection);
        command.Parameters.AddWithValue("siteId", siteId.Value);
        command.Parameters.AddWithValue("visitorId", visitorId.Value);
        command.Parameters.AddWithValue("now", now);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is string kind ? Enum.Parse<VisitorRestrictionKind>(kind) : null;
    }

    public async Task<VisitorRestrictionPage> ListForSiteAsync(
        SiteId siteId, Guid? beforeId, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select vr.id, vr.visitor_id, vr.kind, vr.restricted_at, vr.restricted_by, vr.expires_at,
                   vr.source_conversation_id, vr.lifted_at, vr.lifted_by,
                   v.emoji_creature, v.emoji_food
            from visitor_restrictions vr
            -- `26-202`: left join, not inner - the same reason ConversationReadStore's own equivalent
            -- join is a left join: a visitor row is expected to exist (the FK guarantees it did at
            -- restriction time and visitor rows are never deleted), but this read must never turn a
            -- missing/erased visitor into a missing restriction row.
            left join visitors v on v.id = vr.visitor_id
            where vr.site_id = @siteId and (@beforeId is null or vr.id < @beforeId)
            order by vr.id desc
            limit @limit
            """,
            connection);
        command.Parameters.AddWithValue("siteId", siteId.Value);
        command.Parameters.Add(new NpgsqlParameter("beforeId", NpgsqlDbType.Uuid)
        {
            Value = (object?)beforeId ?? DBNull.Value,
        });
        // One extra row, not returned - the same keyset-paging shape AccessRecordRepository's own
        // remarks describe in full.
        command.Parameters.AddWithValue("limit", limit + 1);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<VisitorRestrictionItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new VisitorRestrictionItem(
                reader.GetGuid(0),
                new VisitorId(reader.GetGuid(1)),
                Enum.Parse<VisitorRestrictionKind>(reader.GetString(2)),
                reader.GetFieldValue<DateTimeOffset>(3),
                new OperatorId(reader.GetGuid(4)),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
                new ConversationId(reader.GetGuid(6)),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
                reader.IsDBNull(8) ? null : new OperatorId(reader.GetGuid(8)),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10)));
        }

        var hasMore = items.Count > limit;
        if (hasMore)
        {
            items.RemoveRange(limit, items.Count - limit);
        }

        var nextBeforeId = hasMore ? items[^1].Id : (Guid?)null;

        return new VisitorRestrictionPage(items, nextBeforeId);
    }
}
