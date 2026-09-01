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
/// <para>Read-only in the strongest available sense: <see cref="ListSitesAsync"/> issues one `SELECT`
/// and this class has no other method. Nothing here writes, and `12-02` deliberately ships no write
/// or action surface for the owner at all.</para>
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
    // 1. `page` picks the page of sites FIRST, by keyset on `id` alone (`SiteOverviewPage`'s own
    //    remarks on why the cursor cannot be `created_at`). `OFFSET` is banned outright
    //    (`data-model.md`), and there is no ORDER BY over an aggregate here to make a cursor
    //    impossible - see ListSitesForOwner on the sort parameter deliberately left out.
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
        with page as (
            select id, name, created_at
            from sites
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

    public async Task<SiteOverviewPage> ListSitesAsync(
        DateTimeOffset recentMessagesSince, Guid? before, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var rows = await connection.QueryAsync<SiteOverviewRow>(new CommandDefinition(
            ListSitesSql,
            new
            {
                Before = before,
                Limit = limit,
                RecentSince = recentMessagesSince,
                DeletedState = DeletedAttachmentState,
            },
            cancellationToken: cancellationToken));

        var items = rows.Select(ToOverviewItem).ToList();

        // Same "a full page implies there may be more" cursor rule every other keyset read here uses
        // (ConversationReadStore) - it can hand back one cursor that yields an empty final page, which
        // is cheaper and simpler than reading limit+1 rows to know for certain.
        var nextBefore = items.Count == limit ? items[^1].Id.Value : (Guid?)null;
        return new SiteOverviewPage(items, nextBefore);
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
}
