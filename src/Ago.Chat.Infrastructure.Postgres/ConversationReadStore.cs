using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Dapper;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>Hand-written SQL over the write model, never through the aggregate (adr/0004) - reads
/// the same <c>messages</c> table <see cref="ConversationRepository"/> writes to, but never via
/// EF's change tracker.</summary>
public sealed class ConversationReadStore(NpgsqlDataSource dataSource) : IConversationReadStore
{
    // Aliased to the record's parameter names - Dapper's constructor-binding matches by name, not
    // by a snake_case-to-PascalCase convention, so without these aliases every row fails to
    // materialize (found by running the integration tests against a real Postgres).
    private const string Sql = """
        select id as "Id", sequence as "Sequence", author_kind as "AuthorKind",
               author_id as "AuthorId", body as "Body", created_at as "CreatedAt",
               attachment_id as "AttachmentId"
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
               attachment_id as "AttachmentId"
        from messages
        where conversation_id = @ConversationId
          and sequence > @AfterSequence
        order by sequence asc
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

    private static MessageHistoryItem ToHistoryItem(MessageRow r) => new(
        new MessageId(r.Id),
        r.Sequence,
        Enum.Parse<MessageAuthorKind>(r.AuthorKind),
        r.AuthorId,
        r.Body,
        new DateTimeOffset(DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc)),
        r.AttachmentId is { } attachmentId ? new AttachmentId(attachmentId) : null);
}
