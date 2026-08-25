using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `12-02`: one row of <see cref="IPlatformOverviewReadStore.ListSitesAsync"/> - one tenant, with the
/// usage signals the platform owner needs to see without opening a `psql` session. A plain projection
/// across five tables, not any aggregate (the same "a read store returns rows, not aggregates" shape
/// <see cref="ConversationSummaryItem"/> already established).
/// </summary>
/// <param name="Id">The `sites` row's own id.</param>
/// <param name="Name">The site display name (`10-02`'s `sites.name`). Empty for a site created before
/// that column existed - the column's own `DEFAULT ''`, not something this query substitutes.</param>
/// <param name="CreatedAt"><see langword="null"/> for a site whose row predates `12-02`'s own
/// `sites.created_at` column (`Site.CreatedAt` has the reasoning: never backfilled, because the
/// system does not know when those rows were created and inventing a value is forbidden).</param>
/// <param name="SeatCount">Rows in `operators` for this site - <b>not</b> distinct `operator_roles`
/// holders, which was the other candidate `12-02` named. An `operators` row is what a seat actually
/// is in this system: it is created once per person who can sign in (`5-05` links it to a Keycloak
/// subject) and it carries the `capacity`/`active_chats` an assignment decision consumes, so its
/// existence - not its role grants - is what a tenant would be billed for. Counting distinct
/// `operator_roles` holders would be wrong in both directions: it drops an operator who holds no role
/// yet (a real state - nothing outside `10-02`'s bootstrap and `1-05`'s seed script grants roles, so
/// a second operator added later has none until a role-management surface exists), and it needs a
/// `DISTINCT` to avoid counting `10-02`'s self-registered operator twice for holding both `"Operator"`
/// and `"Admin"`.</param>
/// <param name="ConversationCount">All-time conversations for this site. Unbounded in time, unlike
/// <paramref name="RecentMessageCount"/> - `conversations` is a single unpartitioned table with a
/// `(site_id, id)` index (`ix_conversations_site_all`, `5-08`), so counting a site's own rows is one
/// index range, not a scan of every tenant's history.</param>
/// <param name="RecentMessageCount">Messages in the bounded recent window the caller asked for -
/// never an all-time count. <see cref="IPlatformOverviewReadStore.ListSitesAsync"/> carries the
/// partitioning reasoning.</param>
/// <param name="LastMessageAt">The most recent message timestamp <b>within that same window</b>, or
/// <see langword="null"/> when the site sent none in it. Deliberately not an all-time maximum: that
/// would have exactly the cost profile the windowed count exists to avoid (`MAX(created_at)` over a
/// site's messages cannot be answered from one partition, so it re-reads every partition that has
/// ever existed). "No activity in the window" and "no messages ever" are therefore the same
/// observation here, which is what the field claims and no more.</param>
/// <param name="AttachmentBytes">`SUM(size_bytes)` over this site's non-deleted attachments, all-time
/// - this is a storage footprint, so a time window would answer a different question. `0` for a site
/// with no attachments (the SQL coalesces; `SUM` over no rows is `NULL`).</param>
public sealed record SiteOverviewItem(
    SiteId Id,
    string Name,
    DateTimeOffset? CreatedAt,
    // `long`, not `int`, for all three counts: Postgres's `count(*)` is `bigint`, and narrowing it
    // here would be a silent decision about a ceiling this system has never measured. Nothing is lost
    // by carrying the type the database actually returns.
    long SeatCount,
    long ConversationCount,
    long RecentMessageCount,
    DateTimeOffset? LastMessageAt,
    long AttachmentBytes);
