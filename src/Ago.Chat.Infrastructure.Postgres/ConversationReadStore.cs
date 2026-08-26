using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Dapper;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>Hand-written SQL over the write model, never through the aggregate (adr/0004) - reads
/// the same <c>messages</c> table <see cref="ConversationRepository"/> writes to, but never via
/// EF's change tracker.
///
/// <para>`17-01`, <b>tenant scope of every query in this file</b>. Two of the three do not mention
/// <c>site_id</c>, on purpose and not by omission: <c>messages</c> carries no <c>site_id</c> column
/// at all (<c>data-model.md</c> - the tenant is reachable only through <c>conversations</c>), so
/// <see cref="GetHistoryAsync"/> and <see cref="GetDeltaAsync"/> are keyed by <c>conversation_id</c>,
/// which is strictly narrower than a site. The caller that guarantees the scope is
/// <c>GetConversationHistoryHandler</c>, and it is the same guarantee for both: the visitor entry
/// points compare <c>conversation.VisitorId</c> against the signed visitor token's own id, and the
/// operator entry points check <c>conversation:read</c> for the caller's site and then require the
/// caller to be the conversation's assigned operator. Neither query is ever reached with a
/// conversation id the caller has not already been proven a party to.
/// <see cref="GetAllForSiteAsync"/> is the one that does filter on <c>site_id</c>, because it is the
/// one whose input is a site rather than a conversation.</para></summary>
public sealed class ConversationReadStore(NpgsqlDataSource dataSource) : IConversationReadStore
{
    // Aliased to the record's parameter names - Dapper's constructor-binding matches by name, not
    // by a snake_case-to-PascalCase convention, so without these aliases every row fails to
    // materialize (found by running the integration tests against a real Postgres).
    private const string Sql = """
        select id as "Id", sequence as "Sequence", author_kind as "AuthorKind",
               author_id as "AuthorId", body as "Body", created_at as "CreatedAt",
               attachment_id as "AttachmentId", client_message_id as "ClientMessageId",
               content_kind as "ContentKind", content as "Payload", actions as "Actions"
        from messages
        where conversation_id = @ConversationId
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
          and sequence > @AfterSequence
        order by sequence asc
        """;

    // `5-08`: keyset on `id` alone - conversation ids are uuid v7 (IIdGenerator), so id order is
    // already creation order, the same single-column cursor GetHistoryAsync uses `sequence` for.
    // No state filter, unlike ix_conversations_waiting - this is the admin's "every conversation"
    // read, backed by the new ix_conversations_site_all index (ConversationConfiguration).
    private const string AllForSiteSql = """
        select id as "Id", visitor_id as "VisitorId", operator_id as "OperatorId", state as "State",
               created_at as "CreatedAt", operator_unread_count as "OperatorUnreadCount"
        from conversations
        where site_id = @SiteId
          and (@BeforeId is null or id < @BeforeId)
        order by id desc
        limit @PageSize
        """;

    public async Task<ConversationHistoryPage> GetHistoryAsync(
        ConversationId conversationId, int? beforeSequence, int pageSize, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var rows = await connection.QueryAsync<MessageRow>(new CommandDefinition(
            Sql,
            new { ConversationId = conversationId.Value, BeforeSequence = beforeSequence, PageSize = pageSize },
            cancellationToken: cancellationToken));

        var items = rows.Select(ToHistoryItem).ToList();

        var nextCursor = items.Count == pageSize ? items[^1].Sequence : (int?)null;
        return new ConversationHistoryPage(items, nextCursor);
    }

    public async Task<IReadOnlyList<MessageHistoryItem>> GetDeltaAsync(
        ConversationId conversationId, int afterSequence, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var rows = await connection.QueryAsync<MessageRow>(new CommandDefinition(
            DeltaSql,
            new { ConversationId = conversationId.Value, AfterSequence = afterSequence },
            cancellationToken: cancellationToken));

        return rows.Select(ToHistoryItem).ToList();
    }

    public async Task<ConversationListPage> GetAllForSiteAsync(
        SiteId siteId, Guid? beforeId, int pageSize, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var rows = await connection.QueryAsync<ConversationSummaryRow>(new CommandDefinition(
            AllForSiteSql,
            new { SiteId = siteId.Value, BeforeId = beforeId, PageSize = pageSize },
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
        r.OperatorUnreadCount);

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
