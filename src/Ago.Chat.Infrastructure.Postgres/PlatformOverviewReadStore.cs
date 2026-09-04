using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Dapper;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `12-02`: the one query in `ago-chat` that reads across every tenant at once, behind
/// <see cref="IPlatformOverviewReadStore"/> (which carries the full "why this is the only one and why
/// it is safe" statement). Hand-written SQL through Dapper, like every other read model here
/// (`adr/0004`) - the cross-tenant scope is the only unusual thing about it, and it is unusual in the
/// `WHERE` clause, not in the mechanism.
///
/// <para>Read-only in the strongest available sense: both methods this class carries issue `SELECT`
/// statements only - <see cref="ListSitesAsync"/> two (counts, then the page), <see cref="GetSiteAsync"/>
/// one. Nothing here writes, and `12-02`/`23-14` deliberately ship no write or action surface for the
/// owner's *reads* at all (`22-17`'s owner grant/revoke are writes, but they live behind a different
/// port - `EnabledModule`'s own aggregate, never this read store).</para>
/// </summary>
public sealed class PlatformOverviewReadStore(NpgsqlDataSource dataSource) : IPlatformOverviewReadStore
{
    /// <summary>
    /// The `attachments.state` value that is excluded. Compared as the CLR enum member name because
    /// that is exactly what EF writes (`AttachmentConfiguration`'s `HasConversion&lt;string&gt;()`),
    /// not `data-model.md`'s lowercase prose spelling - built as an interpolated constant off
    /// <see cref="AttachmentState.Deleted"/> so a rename of the enum member cannot silently leave this
    /// filter matching a value the table no longer contains.
    /// </summary>
    private static readonly string DeletedAttachmentState = nameof(AttachmentState.Deleted);

    // Shape, and why it is this shape:
    //
    // 1. `matching` narrows the candidate set by `23-14`'s own search predicate FIRST, and `page` picks
    //    the keyset page from that already-narrowed set, by `id` alone (`SiteOverviewPage`'s own
    //    remarks on why the cursor cannot be `created_at`). `OFFSET` is banned outright
    //    (`data-model.md`), and there is no ORDER BY over an aggregate here to make a cursor
    //    impossible - see ListSitesForOwner on the sort parameter deliberately left out. Filtering
    //    before paging, rather than after, is what makes a search and the cursor compose: paging an
    //    unfiltered set and discarding non-matches per page would make `limit` mean something different
    //    for a search than for the ordinary list.
    //
    // 2. Every usage signal is then computed PER PAGE ROW, not for all sites and then filtered. The
    //    number of aggregate evaluations per request is therefore bounded by `limit`, not by how many
    //    tenants exist - the property that keeps this endpoint's cost flat as the business grows,
    //    which is the whole point of an operations view.
    //
    // 3. `15-09`/`adr/0087`: `messages` is now `PARTITION BY HASH (site_id)`, not `RANGE (created_at)` -
    //    `m.site_id = p.id` (added in this item) is what prunes the lateral's own messages scan to the
    //    one bucket this row's site lives in; `created_at` carries no pruning power any more. Before
    //    this item the predicate order was reversed - `@RecentSince` on `created_at` was what pruned,
    //    and the join to `conversations` was the only way to reach a tenant at all, since `messages`
    //    had no `site_id` column yet. Both predicates stay: `m.site_id = p.id` prunes the partition,
    //    `m.created_at >= @RecentSince` bounds the scan *within* that one already-pruned bucket to a
    //    recent window instead of the tenant's entire history, via the composite `(site_id, created_at)`
    //    index - still load-bearing, now for index selectivity rather than partition pruning. An
    //    all-time COUNT(*) would still scan the tenant's whole history and get slower every month the
    //    deployment stays alive. `max(created_at)` rides along inside that same bounded scan for the
    //    identical reason - an all-time "last activity" is exactly the unbounded read the count avoids,
    //    so this reports last activity WITHIN the window and null otherwise (`SiteOverviewItem.LastMessageAt`
    //    states that plainly rather than letting the name imply more).
    //
    // 4. The messages side also joins through `conversations`, even though `messages` has carried its
    //    own `site_id` since `18-01` - `c.site_id = p.id` and `m.site_id = p.id` are redundant with each
    //    other for correctness (both true of the identical rows) but not for planning: keeping the
    //    `conversations` filter lets the planner use `ix_conversations_site_all` (`5-08`) to size the
    //    join's other side, while `m.site_id = p.id` is what a partitioned-table scan needs regardless
    //    of what the other side of a join can prove. Each conversation's messages are then found on the
    //    `(conversation_id, sequence, site_id)` unique index (`15-09`'s own widening of `2-06`'s
    //    original).
    //
    // 5. `sum(size_bytes)` is cast to bigint: Postgres's `sum` over a `bigint` column returns
    //    `numeric`, which Dapper would refuse to bind to a `long` field. `coalesce` turns "no
    //    attachments" into 0 rather than null - "this tenant stores nothing" is a real 0, not a
    //    missing value, unlike `last_message_at` where null genuinely means "nothing to report".
    private const string ListSitesSql = """
        with matching as (
            select id, name, created_at
            from sites
            where (@Pattern is null or name ilike @Pattern or cast(id as text) ilike @Pattern)
        ),
        page as (
            select id, name, created_at
            from matching
            where (@Before is null or id < @Before)
            order by id desc
            limit @Limit
        )
        select p.id as "Id",
               p.name as "Name",
               p.created_at as "CreatedAt",
               (select count(*) from operators o where o.site_id = p.id) as "SeatCount",
               (select count(*) from conversations c where c.site_id = p.id) as "ConversationCount",
               recent.message_count as "RecentMessageCount",
               recent.last_message_at as "LastMessageAt",
               (select coalesce(sum(a.size_bytes), 0)::bigint
                from attachments a
                where a.site_id = p.id and a.state <> @DeletedState) as "AttachmentBytes"
        from page p
        left join lateral (
            select count(*) as message_count, max(m.created_at) as last_message_at
            from conversations c
            join messages m on m.conversation_id = c.id
            where c.site_id = p.id and m.site_id = p.id and m.created_at >= @RecentSince
        ) recent on true
        order by p.id desc
        """;

    // `23-14`: counted separately from `ListSitesSql`, over the *whole* table rather than the page -
    // this is precisely what keeps "how many matched" honest when a search returns fewer rows than
    // `@Limit`, or none at all. `count(*) filter (where ...)` reuses the identical predicate `matching`
    // above narrows by, so the two queries can never disagree about what "matches" means; `TotalSites`
    // needs no predicate at all, since it is the fixed denominator a caller compares the filtered count
    // against regardless of what was searched for.
    private const string CountsSql = """
        select
            count(*) as "TotalSites",
            count(*) filter (where @Pattern is null or name ilike @Pattern or cast(id as text) ilike @Pattern)
                as "MatchingSites"
        from sites
        """;

    private const string GetSiteSql = """
        select p.id as "Id",
               p.name as "Name",
               p.created_at as "CreatedAt",
               (select count(*) from operators o where o.site_id = p.id) as "SeatCount",
               (select count(*) from conversations c where c.site_id = p.id) as "ConversationCount",
               recent.message_count as "RecentMessageCount",
               recent.last_message_at as "LastMessageAt",
               (select coalesce(sum(a.size_bytes), 0)::bigint
                from attachments a
                where a.site_id = p.id and a.state <> @DeletedState) as "AttachmentBytes"
        from sites p
        left join lateral (
            select count(*) as message_count, max(m.created_at) as last_message_at
            from conversations c
            join messages m on m.conversation_id = c.id
            where c.site_id = p.id and m.site_id = p.id and m.created_at >= @RecentSince
        ) recent on true
        where p.id = @SiteId
        """;

    public async Task<SiteOverviewPage> ListSitesAsync(
        DateTimeOffset recentMessagesSince, string? query, Guid? before, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var parameters = new
        {
            Pattern = BuildLikePattern(query),
            Before = before,
            Limit = limit,
            RecentSince = recentMessagesSince,
            DeletedState = DeletedAttachmentState,
        };

        // Two round trips on the same connection, not one multi-statement command: `CountsSql` has no
        // `@Before`/`@Limit` use, and keeping the two queries textually separate is what lets each be
        // read (and EXPLAINed) on its own rather than as one query whose two halves share nothing.
        // Both are cheap - `sites` is not partitioned and every real deployment this runs against today
        // is small enough that a human reads the result by hand (`ListSitesForOwnerHandler`'s own
        // remarks on this endpoint's frequency).
        var counts = await connection.QuerySingleAsync<SiteCountsRow>(new CommandDefinition(
            CountsSql, parameters, cancellationToken: cancellationToken));

        var rows = await connection.QueryAsync<SiteOverviewRow>(new CommandDefinition(
            ListSitesSql, parameters, cancellationToken: cancellationToken));

        var items = rows.Select(ToOverviewItem).ToList();

        // Same "a full page implies there may be more" cursor rule every other keyset read here uses
        // (ConversationReadStore) - it can hand back one cursor that yields an empty final page, which
        // is cheaper and simpler than reading limit+1 rows to know for certain.
        var nextBefore = items.Count == limit ? items[^1].Id.Value : (Guid?)null;
        return new SiteOverviewPage(items, nextBefore, counts.MatchingSites, counts.TotalSites);
    }

    public async Task<SiteOverviewItem?> GetSiteAsync(
        SiteId siteId, DateTimeOffset recentMessagesSince, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var row = await connection.QuerySingleOrDefaultAsync<SiteOverviewRow?>(new CommandDefinition(
            GetSiteSql,
            new { SiteId = siteId.Value, RecentSince = recentMessagesSince, DeletedState = DeletedAttachmentState },
            cancellationToken: cancellationToken));

        return row is null ? null : ToOverviewItem(row);
    }

    /// <summary>`23-14`: turns a raw search string into an `ILIKE` pattern, or <see langword="null"/>
    /// for "no filter" - the one place either SQL statement's `@Pattern is null` branch is decided.
    /// `%`/`_`/`\` are escaped so a tenant name that happens to contain one of LIKE's own wildcard
    /// characters is matched literally rather than interpreted; Postgres's default `LIKE`/`ILIKE`
    /// escape character is already backslash, so no explicit `ESCAPE` clause is needed on the SQL side.
    /// Substring, not prefix: it matches the site's name <i>or</i> its id cast to text
    /// (`ListSitesForOwner`'s own remarks on why an id search is not required to start at the
    /// beginning), which also covers `ui-inventory.md` §8.1's own 8-hex-character id badge - that badge
    /// is always a leading substring of the full id text, so searching it finds the row without this
    /// method needing to know the badge is only 8 characters wide.</summary>
    private static string? BuildLikePattern(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var escaped = query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        return $"%{escaped}%";
    }

    private static SiteOverviewItem ToOverviewItem(SiteOverviewRow r) => new(
        new SiteId(r.Id),
        r.Name,
        ToUtc(r.CreatedAt),
        r.SeatCount,
        r.ConversationCount,
        r.RecentMessageCount,
        ToUtc(r.LastMessageAt),
        r.AttachmentBytes);

    private static DateTimeOffset? ToUtc(DateTime? value) =>
        value is { } present ? new DateTimeOffset(DateTime.SpecifyKind(present, DateTimeKind.Utc)) : null;

    /// <summary>`23-14`: the two-count row <see cref="CountsSql"/> materializes - its own type rather
    /// than reusing <see cref="SiteOverviewRow"/>, since the two queries share no columns.</summary>
    private sealed record SiteCountsRow(long TotalSites, long MatchingSites);
}
