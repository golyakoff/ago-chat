using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Dapper;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>Hand-written SQL over the write model, never through the aggregate (adr/0004) - reads
/// the same <c>messages</c> table <see cref="ConversationRepository"/> writes to, but never via
/// EF's change tracker.
///
/// <para>`15-09`/`adr/0087`, <b>tenant scope of every query in this file.</b> `messages` is now
/// `PARTITION BY HASH (site_id)`, so every query against it - not only <see cref="GetAllForSiteAsync"/>,
/// which reads `conversations` and always has - must filter `site_id` or it silently visits all 64
/// buckets. <see cref="GetHistoryAsync"/> and <see cref="GetDeltaAsync"/> were, before this item, keyed
/// by `conversation_id` alone: correct (the caller - `GetConversationHistoryHandler` - already proves
/// the caller a party to that exact conversation before either is reached: the visitor entry points
/// compare `conversation.VisitorId` against the signed visitor token's own id, the operator entry points
/// check `conversation:read` for the caller's site and then require the caller to be the conversation's
/// assigned operator) but with no pruning benefit at all under hash partitioning, since
/// `conversation_id` does not identify a bucket. Both methods now take `siteId` as well - narrower than
/// nothing changed about the authorization story, only about what the query plan can prune to - and the
/// caller already has it on the `Conversation` it just loaded, so this costs nothing at the call
/// site.</para>
///
/// <para><see cref="GetVisitorHistoryAsync"/>'s own `messages` lateral is scoped the same way, but
/// without a new parameter: it correlates against `c.site_id` (the outer `conversations` row already
/// selected), since a visitor - and therefore every conversation and message reachable from one - always
/// belongs to exactly one site (`18-07`'s own remarks on this file).</para></summary>
public sealed class ConversationReadStore(NpgsqlDataSource dataSource) : IConversationReadStore
{
    // Aliased to the record's parameter names - Dapper's constructor-binding matches by name, not
    // by a snake_case-to-PascalCase convention, so without these aliases every row fails to
    // materialize (found by running the integration tests against a real Postgres).
    // internal, not private: MessagePartitionPruningExplainTests (15-09/adr/0087) runs `EXPLAIN`
    // against this exact text, via Ago.Chat.Infrastructure.Postgres's own InternalsVisibleTo - the
    // pruning proof has to exercise the real production SQL, not a hand-copied approximation of it
    // that could silently drift from what actually ships.
    internal const string Sql = """
        select id as "Id", sequence as "Sequence", author_kind as "AuthorKind",
               author_id as "AuthorId", body as "Body", created_at as "CreatedAt",
               attachment_id as "AttachmentId", client_message_id as "ClientMessageId",
               content_kind as "ContentKind", content as "Payload", actions as "Actions"
        from messages
        where conversation_id = @ConversationId
          and site_id = @SiteId
          and (@BeforeSequence is null or sequence < @BeforeSequence)
        order by sequence desc
        limit @PageSize
        """;

    // `3-03`: forward, unbounded, no LIMIT - see IConversationReadStore.GetDeltaAsync's remarks on
    // why this direction does not need keyset paging the way GetHistoryAsync's backward one does.
    private const string DeltaSql = """
        select id as "Id", sequence as "Sequence", author_kind as "AuthorKind",
               author_id as "AuthorId", body as "Body", created_at as "CreatedAt",
               attachment_id as "AttachmentId", client_message_id as "ClientMessageId",
               content_kind as "ContentKind", content as "Payload", actions as "Actions"
        from messages
        where conversation_id = @ConversationId
          and site_id = @SiteId
          and sequence > @AfterSequence
        order by sequence asc
        """;

    // `5-08`: keyset on `id` alone - conversation ids are uuid v7 (IIdGenerator), so id order is
    // already creation order, the same single-column cursor GetHistoryAsync uses `sequence` for.
    // No state filter, unlike ix_conversations_waiting - this is the admin's "every conversation"
    // read, backed by the new ix_conversations_site_all index (ConversationConfiguration).
    // `18-04`: the `exists(...)` clause is only ever added in spirit - Dapper always sends
    // @TagId (null when unfiltered), and `@TagId is null or exists(...)` short-circuits to a plain
    // site-scoped scan for the unfiltered case, the same "one statement handles both" shape
    // `GetHistoryAsync`'s own `@BeforeSequence is null or ...` clause already uses on this file.
    // `23-02`: the one `ConversationSummaryItem` caller that renders an operator's name to a human
    // (the admin/supervisor site-wide list) - `left join`, not inner, so a conversation whose operator
    // has since been removed still lists, with a blank name rather than vanishing from the page.
    private const string AllForSiteSql = """
        select c.id as "Id", c.visitor_id as "VisitorId", c.operator_id as "OperatorId", c.state as "State",
               c.created_at as "CreatedAt", c.operator_unread_count as "OperatorUnreadCount", c.outcome as "Outcome",
               op.display_name as "OperatorName"
        from conversations c
        left join operators op on op.id = c.operator_id
        where c.site_id = @SiteId
          and (@BeforeId is null or c.id < @BeforeId)
          and (@TagId is null or exists(
              select 1 from conversation_tags ct where ct.conversation_id = c.id and ct.tag_id = @TagId))
        order by c.id desc
        limit @PageSize
        """;

    public async Task<ConversationHistoryPage> GetHistoryAsync(
        ConversationId conversationId, SiteId siteId, int? beforeSequence, int pageSize, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var rows = await connection.QueryAsync<MessageRow>(new CommandDefinition(
            Sql,
            new { ConversationId = conversationId.Value, SiteId = siteId.Value, BeforeSequence = beforeSequence, PageSize = pageSize },
            cancellationToken: cancellationToken));

        var items = rows.Select(ToHistoryItem).ToList();

        var nextCursor = items.Count == pageSize ? items[^1].Sequence : (int?)null;
        return new ConversationHistoryPage(items, nextCursor);
    }

    public async Task<IReadOnlyList<MessageHistoryItem>> GetDeltaAsync(
        ConversationId conversationId, SiteId siteId, int afterSequence, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var rows = await connection.QueryAsync<MessageRow>(new CommandDefinition(
            DeltaSql,
            new { ConversationId = conversationId.Value, SiteId = siteId.Value, AfterSequence = afterSequence },
            cancellationToken: cancellationToken));

        return rows.Select(ToHistoryItem).ToList();
    }

    // `16-02`: the same row shape as AllForSiteSql above, filtered to one id instead of paged - see
    // IConversationReadStore.GetByIdAsync's own remarks on why this is a separate statement rather
    // than GetAllForSiteAsync with an extra filter bolted on.
    // `23-02`: the same `left join operators` `AllForSiteSql` above gains - added here too rather than
    // left to Dapper's own optional-constructor-parameter default, so `ConversationSummaryRow` never
    // depends on that behaviour going unverified for a single-row query where the join costs nothing
    // real.
    private const string ByIdSql = """
        select c.id as "Id", c.visitor_id as "VisitorId", c.operator_id as "OperatorId", c.state as "State",
               c.created_at as "CreatedAt", c.operator_unread_count as "OperatorUnreadCount", c.outcome as "Outcome",
               op.display_name as "OperatorName"
        from conversations c
        left join operators op on op.id = c.operator_id
        where c.id = @ConversationId and c.site_id = @SiteId
        """;

    public async Task<ConversationSummaryItem?> GetByIdAsync(
        ConversationId conversationId, SiteId siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var row = await connection.QuerySingleOrDefaultAsync<ConversationSummaryRow>(new CommandDefinition(
            ByIdSql,
            new { ConversationId = conversationId.Value, SiteId = siteId.Value },
            cancellationToken: cancellationToken));

        return row is null ? null : ToSummaryItem(row);
    }

    // `18-07`: the visitor-history panel's own read. `visitor_id` is the whole filter -
    // ChannelIdentityConfiguration's remarks explain why a Visitor (and therefore every conversation
    // hanging off one) already belongs to exactly one Site, so this needs no separate site_id check
    // the way GetAllForSiteAsync does. The `LEFT JOIN LATERAL` picks each conversation's own last
    // message (by `sequence`, matching every other ordering in this file - never `created_at`,
    // adr/0011) without a second round trip or an N+1 query per row.
    // `15-09`/`adr/0087`: the lateral's own `m.site_id = c.site_id` needs no new bind parameter -
    // `c.site_id` is already selected by the outer query, correlated per row, so Postgres can use it
    // for runtime partition pruning even though this whole query has no single fixed site_id to bind
    // (the same per-row correlation PlatformOverviewReadStore's own cross-tenant lateral already
    // relies on, this file's own remarks explain why).
    private const string VisitorHistorySql = """
        select c.id as "Id", c.state as "State", c.created_at as "StartedAt", c.closed_at as "ClosedAt",
               lm.body as "PreviewBody", lm.author_kind as "PreviewAuthorKind", lm.created_at as "PreviewCreatedAt"
        from conversations c
        left join lateral (
            select body, author_kind, created_at
            from messages m
            where m.conversation_id = c.id
              and m.site_id = c.site_id
            order by m.sequence desc
            limit 1
        ) lm on true
        where c.visitor_id = @VisitorId
          and c.id <> @ExcludeConversationId
          and (@BeforeId is null or c.id < @BeforeId)
        order by c.id desc
        limit @PageSize
        """;

    public async Task<VisitorHistoryPage> GetVisitorHistoryAsync(
        VisitorId visitorId, ConversationId excludeConversationId, Guid? beforeId, int pageSize,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var rows = await connection.QueryAsync<VisitorHistoryRow>(new CommandDefinition(
            VisitorHistorySql,
            new
            {
                VisitorId = visitorId.Value,
                ExcludeConversationId = excludeConversationId.Value,
                BeforeId = beforeId,
                PageSize = pageSize,
            },
            cancellationToken: cancellationToken));

        var items = rows.Select(ToVisitorHistoryItem).ToList();

        var nextCursor = items.Count == pageSize ? items[^1].Id.Value : (Guid?)null;
        return new VisitorHistoryPage(items, nextCursor);
    }

    public async Task<ConversationListPage> GetAllForSiteAsync(
        SiteId siteId, Guid? beforeId, int pageSize, TagId? tagId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var rows = await connection.QueryAsync<ConversationSummaryRow>(new CommandDefinition(
            AllForSiteSql,
            new { SiteId = siteId.Value, BeforeId = beforeId, PageSize = pageSize, TagId = tagId.HasValue ? tagId.Value.Value : (Guid?)null },
            cancellationToken: cancellationToken));

        var items = rows.Select(ToSummaryItem).ToList();

        var nextCursor = items.Count == pageSize ? items[^1].Id.Value : (Guid?)null;
        return new ConversationListPage(items, nextCursor);
    }

    private static ConversationSummaryItem ToSummaryItem(ConversationSummaryRow r) => new(
        new ConversationId(r.Id),
        new VisitorId(r.VisitorId),
        r.OperatorId is { } operatorId ? new OperatorId(operatorId) : null,
        r.State,
        new DateTimeOffset(DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc)),
        r.OperatorUnreadCount,
        r.Outcome,
        r.OperatorName);

    private static VisitorHistoryItem ToVisitorHistoryItem(VisitorHistoryRow r) => new(
        new ConversationId(r.Id),
        r.State,
        new DateTimeOffset(DateTime.SpecifyKind(r.StartedAt, DateTimeKind.Utc)),
        r.ClosedAt is { } closedAt ? new DateTimeOffset(DateTime.SpecifyKind(closedAt, DateTimeKind.Utc)) : null,
        r.PreviewBody,
        r.PreviewAuthorKind is { } authorKind ? Enum.Parse<MessageAuthorKind>(authorKind) : null,
        r.PreviewCreatedAt is { } previewCreatedAt
            ? new DateTimeOffset(DateTime.SpecifyKind(previewCreatedAt, DateTimeKind.Utc))
            : null);

    private static MessageHistoryItem ToHistoryItem(MessageRow r) => new(
        new MessageId(r.Id),
        r.Sequence,
        Enum.Parse<MessageAuthorKind>(r.AuthorKind),
        r.AuthorId,
        r.Body,
        new DateTimeOffset(DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc)),
        r.AttachmentId is { } attachmentId ? new AttachmentId(attachmentId) : null,
        r.ClientMessageId,
        r.ContentKind,
        r.Payload,
        r.Actions);
}
