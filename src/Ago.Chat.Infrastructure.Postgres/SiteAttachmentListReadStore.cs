using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Dapper;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `23-80`'s <see cref="ISiteAttachmentListReadStore"/> - a Dapper read store (adr/0004) over
/// <c>attachments</c>, joined to <c>messages</c> for the sender. The join is on
/// <c>m.id = a.message_id AND m.site_id = a.site_id</c>, never <c>m.id</c> alone - <c>messages</c> is
/// hash-partitioned by <c>site_id</c> (`data-model.md`), so carrying the partition key into the join
/// predicate lets Postgres prune to the one partition this query's own <c>WHERE a.site_id = @SiteId</c>
/// already names, rather than probing all 64.
///
/// <para><b>"Is this a duplicate" is a per-row <c>EXISTS</c> against the partial index
/// <c>ix_attachments_site_content_hash</c> (`23-76`), not a window function over the whole result
/// set.</b> A window <c>COUNT(*) OVER (PARTITION BY content_hash)</c> would need to see every one of a
/// tenant's <c>Ready</c> rows before it could apply <c>LIMIT</c>, paid on every page of every sort
/// order; the <c>EXISTS</c> form only runs for the rows actually being returned (an ordinary list page)
/// or, for the <see cref="AttachmentListFilterKind.Duplicates"/> filter, drives an index-backed
/// semi-join Postgres can push down - the identical index the dedup lookup itself
/// (<c>AttachmentRepository.FindReadyDuplicateAsync</c>) already relies on, reused rather than
/// duplicated.</para>
///
/// <para><b>Keyset pagination, one sort order at a time</b> (data-model.md: "OFFSET is banned"). Each
/// <see cref="AttachmentListSort"/> member gets its own <c>ORDER BY</c>/cursor-predicate pair below,
/// always tie-broken by <c>a.id</c> so two attachments that compare equal on the sort key alone (the
/// same size, the same content type, ...) still produce a stable, gap-free page boundary. Built by a
/// `switch` over a closed, five-member enum - never by concatenating a caller-supplied column name -
/// so nothing here is a SQL-injection surface despite the query shape changing per request.</para>
/// </summary>
public sealed class SiteAttachmentListReadStore(NpgsqlDataSource dataSource) : ISiteAttachmentListReadStore
{
    private const string SelectColumns = """
        a.id AS "Id", a.conversation_id AS "ConversationId", a.content_type AS "ContentType",
        a.size_bytes AS "SizeBytes", a.created_at AS "CreatedAt", a.download_count AS "DownloadCount",
        a.last_downloaded_at AS "LastDownloadedAt", m.author_kind AS "SenderKind", m.author_id AS "SenderId",
        EXISTS (
            SELECT 1 FROM attachments d
            WHERE d.site_id = a.site_id AND d.content_hash = a.content_hash
                AND d.state = 'Ready' AND d.id <> a.id
        ) AS "IsDuplicate"
        """;

    private const string FromClause = """
        FROM attachments a
        LEFT JOIN messages m ON m.id = a.message_id AND m.site_id = a.site_id
        WHERE a.site_id = @SiteId AND a.state = 'Ready'
        """;

    public async Task<AttachmentListPage> ListAsync(
        SiteId siteId,
        AttachmentListSort sort,
        AttachmentListFilterKind filter,
        AttachmentListCursor? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        var parameters = new DynamicParameters();
        parameters.Add("SiteId", siteId.Value);
        // Fetch one extra row - present iff a next page exists, trimmed off below rather than
        // returned. The alternative (always emitting a cursor and letting the next call come back
        // empty) would cost a whole extra round trip just to learn what this one row already knows.
        parameters.Add("Limit", limit + 1);

        var filterSql = BuildFilterSql(filter);
        var (orderBySql, cursorSql) = BuildSortSql(sort, cursor, parameters);

        var sql = $"""
            SELECT {SelectColumns}
            {FromClause}
            {filterSql}
            {cursorSql}
            ORDER BY {orderBySql}
            LIMIT @Limit
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<AttachmentListRow>(new CommandDefinition(
            sql, parameters, cancellationToken: cancellationToken))).ToList();

        var hasMore = rows.Count > limit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var items = rows.Select(ToItem).ToList();
        var nextCursor = hasMore && rows.Count > 0 ? BuildCursor(sort, rows[^1]) : null;

        return new AttachmentListPage(items, nextCursor);
    }

    public async Task<IReadOnlyList<LargestConversationItem>> ListLargestConversationsAsync(
        SiteId siteId, int limit, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT a.conversation_id AS "ConversationId", SUM(a.size_bytes) AS "TotalBytes", COUNT(*) AS "AttachmentCount"
            FROM attachments a
            WHERE a.site_id = @SiteId AND a.state = 'Ready'
            GROUP BY a.conversation_id
            ORDER BY SUM(a.size_bytes) DESC
            LIMIT @Limit
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<LargestConversationRow>(new CommandDefinition(
            sql, new { SiteId = siteId.Value, Limit = limit }, cancellationToken: cancellationToken));

        return rows
            .Select(r => new LargestConversationItem(new ConversationId(r.ConversationId), r.TotalBytes, r.AttachmentCount))
            .ToList();
    }

    private static string BuildFilterSql(AttachmentListFilterKind filter) => filter switch
    {
        AttachmentListFilterKind.None => string.Empty,
        AttachmentListFilterKind.NeverDownloaded => "AND a.download_count = 0",
        // Reuses the identical EXISTS shape SelectColumns already computes for `IsDuplicate` - kept
        // as a second, separate predicate rather than `AND "IsDuplicate"` because that alias is not
        // visible to the WHERE clause evaluating the same SELECT list it is defined in.
        AttachmentListFilterKind.Duplicates =>
            """
            AND a.content_hash IS NOT NULL AND EXISTS (
                SELECT 1 FROM attachments d2
                WHERE d2.site_id = a.site_id AND d2.content_hash = a.content_hash
                    AND d2.state = 'Ready' AND d2.id <> a.id
            )
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, "Unknown attachment list filter."),
    };

    /// <summary>Returns the `ORDER BY` expression and the cursor's own `WHERE` fragment (empty when
    /// <paramref name="cursor"/> is null - the first page). Adds whatever typed parameter the chosen
    /// sort's cursor comparison needs to <paramref name="parameters"/> directly, since each sort
    /// compares a different column type.</summary>
    private static (string OrderBy, string CursorSql) BuildSortSql(
        AttachmentListSort sort, AttachmentListCursor? cursor, DynamicParameters parameters)
    {
        switch (sort)
        {
            case AttachmentListSort.SizeDescending:
                if (cursor is not null)
                {
                    parameters.Add("CursorSize", long.Parse(cursor.Value, System.Globalization.CultureInfo.InvariantCulture));
                    parameters.Add("CursorId", cursor.AttachmentId);
                }

                return (
                    "a.size_bytes DESC, a.id DESC",
                    cursor is null ? string.Empty : "AND (a.size_bytes, a.id) < (@CursorSize, @CursorId)");

            case AttachmentListSort.TypeAscending:
                if (cursor is not null)
                {
                    parameters.Add("CursorType", cursor.Value);
                    parameters.Add("CursorId", cursor.AttachmentId);
                }

                return (
                    "a.content_type ASC, a.id ASC",
                    cursor is null ? string.Empty : "AND (a.content_type, a.id) > (@CursorType, @CursorId)");

            case AttachmentListSort.AgeAscending:
                if (cursor is not null)
                {
                    parameters.Add("CursorCreatedAt", DateTimeOffset.Parse(cursor.Value, System.Globalization.CultureInfo.InvariantCulture));
                    parameters.Add("CursorId", cursor.AttachmentId);
                }

                return (
                    "a.created_at ASC, a.id ASC",
                    cursor is null ? string.Empty : "AND (a.created_at, a.id) > (@CursorCreatedAt, @CursorId)");

            case AttachmentListSort.ConversationAscending:
                if (cursor is not null)
                {
                    parameters.Add("CursorConversationId", Guid.Parse(cursor.Value));
                    parameters.Add("CursorId", cursor.AttachmentId);
                }

                return (
                    "a.conversation_id ASC, a.id ASC",
                    cursor is null ? string.Empty : "AND (a.conversation_id, a.id) > (@CursorConversationId, @CursorId)");

            case AttachmentListSort.SenderAscending:
                if (cursor is not null)
                {
                    parameters.Add("CursorSender", cursor.Value);
                    parameters.Add("CursorId", cursor.AttachmentId);
                }

                // `'zzzz'` sorts after every real MessageAuthorKind member - see
                // AttachmentListSort.SenderAscending's own remarks in ISiteAttachmentListReadStore for
                // why an unlinked attachment (no sender known) reads as "last," not as an
                // undefined-ordering NULL row-comparison edge case.
                return (
                    "COALESCE(m.author_kind, 'zzzz') ASC, a.id ASC",
                    cursor is null ? string.Empty : "AND (COALESCE(m.author_kind, 'zzzz'), a.id) > (@CursorSender, @CursorId)");

            default:
                throw new ArgumentOutOfRangeException(nameof(sort), sort, "Unknown attachment list sort.");
        }
    }

    private static AttachmentListCursor BuildCursor(AttachmentListSort sort, AttachmentListRow lastRow)
    {
        var value = sort switch
        {
            AttachmentListSort.SizeDescending => lastRow.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            AttachmentListSort.TypeAscending => lastRow.ContentType,
            AttachmentListSort.AgeAscending => lastRow.CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            AttachmentListSort.ConversationAscending => lastRow.ConversationId.ToString(),
            AttachmentListSort.SenderAscending => lastRow.SenderKind ?? "zzzz",
            _ => throw new ArgumentOutOfRangeException(nameof(sort), sort, "Unknown attachment list sort."),
        };

        return new AttachmentListCursor(value, lastRow.Id);
    }

    private static AttachmentListItem ToItem(AttachmentListRow r) => new(
        new AttachmentId(r.Id),
        new ConversationId(r.ConversationId),
        r.ContentType,
        r.SizeBytes,
        r.CreatedAt,
        r.DownloadCount,
        r.LastDownloadedAt,
        r.SenderKind is { } senderKind ? Enum.Parse<MessageAuthorKind>(senderKind) : null,
        r.SenderId,
        r.IsDuplicate);

    // `EnabledModuleReadStore`'s own precedent: a mutable class with `init` properties, not a
    // positional `record` - Dapper materializes this by property name via its default mapper, which
    // is the reliable path for a nested private row type; a record's constructor-matching path
    // stumbled on the nullable sender columns here (found by running the real fault-injection test
    // against a real Postgres, not assumed).
    private sealed class AttachmentListRow
    {
        public Guid Id { get; init; }

        public Guid ConversationId { get; init; }

        public string ContentType { get; init; } = string.Empty;

        public long SizeBytes { get; init; }

        public DateTimeOffset CreatedAt { get; init; }

        public long DownloadCount { get; init; }

        public DateTimeOffset? LastDownloadedAt { get; init; }

        public string? SenderKind { get; init; }

        public Guid? SenderId { get; init; }

        public bool IsDuplicate { get; init; }
    }

    private sealed class LargestConversationRow
    {
        public Guid ConversationId { get; init; }

        public long TotalBytes { get; init; }

        public int AttachmentCount { get; init; }
    }
}
