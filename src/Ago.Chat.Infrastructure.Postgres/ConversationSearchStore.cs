using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Dapper;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `18-01`: hand-written SQL over the write model (adr/0004), the same split every other read store
/// in this file's neighbourhood uses. Unlike <see cref="ConversationReadStore"/>'s history/delta
/// queries, this one filters `messages.site_id` directly rather than reaching the tenant through
/// `conversation_id` - see <see cref="IConversationSearchStore"/>'s own remarks on why that
/// difference is the entire point of this item.
///
/// <para><b>Postgres full-text, `'simple'` configuration.</b> `'simple'` tokenizes without stemming
/// or a language-specific dictionary - a deliberately conservative choice for a product whose
/// tenants and visitors are not assumed to write in one language (`11-10`'s locale support already
/// spans several). A stemmed, language-aware configuration (`'english'`, `'russian'`, ...) would
/// match more generously within one language but requires knowing which language a given message is
/// in - a real feature this item does not build. `'simple'` is exact-token matching, not a worse
/// version of a same feature; language-aware ranking is future work, not a corner cut here.</para>
///
/// <para><b><c>from</c>/<c>to</c> are what makes this prune.</b> `messages` is `RANGE (created_at)`
/// partitioned monthly (`2-06`) - a query that does not bound `created_at` touches every partition
/// regardless of how selective its other predicates are. The two indexes this query relies on
/// (composite `(site_id, created_at)` and the full-text GIN, both per leaf partition) are built and
/// maintained by <c>Ago.Chat.Worker.MessageSearchIndexJob</c>, never by this class or by a
/// migration - see that job's own remarks for why.</para>
/// </summary>
public sealed class ConversationSearchStore(NpgsqlDataSource dataSource) : IConversationSearchStore
{
    // Aliased to the record's parameter names, matching ConversationReadStore's own convention -
    // Dapper's constructor binding matches by name, not by a snake_case-to-PascalCase convention.
    // `plainto_tsquery` rather than `websearch_to_tsquery`: the operator types a phrase, not a search
    // syntax, and `plainto_tsquery` is the member of Postgres's own tsquery family built for exactly
    // that ("this whole string is what I'm looking for", ANDing its tokens together) - `websearch_to_
    // tsquery` would additionally interpret quotes/OR/minus an operator never asked this UI to
    // support.
    private const string Sql = """
        select m.id as "MessageId", m.conversation_id as "ConversationId", m.sequence as "Sequence",
               m.body as "MatchedBody", m.author_kind as "AuthorKind", m.created_at as "CreatedAt",
               c.state as "ConversationState"
        from messages m
        join conversations c on c.id = m.conversation_id
        where m.site_id = @SiteId
          and m.created_at >= @From
          and m.created_at < @To
          and to_tsvector('simple', m.body) @@ plainto_tsquery('simple', @Phrase)
          and (@BeforeMessageId is null or m.id < @BeforeMessageId)
        order by m.id desc
        limit @PageSize
        """;

    public async Task<ConversationSearchPage> SearchAsync(
        SiteId siteId,
        string phrase,
        DateTimeOffset from,
        DateTimeOffset to,
        Guid? beforeMessageId,
        int pageSize,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var rows = await connection.QueryAsync<SearchResultRow>(new CommandDefinition(
            Sql,
            new
            {
                SiteId = siteId.Value,
                Phrase = phrase,
                From = from,
                To = to,
                BeforeMessageId = beforeMessageId,
                PageSize = pageSize,
            },
            cancellationToken: cancellationToken));

        var items = rows.Select(ToResultItem).ToList();

        var nextCursor = items.Count == pageSize ? items[^1].MessageId.Value : (Guid?)null;
        return new ConversationSearchPage(items, nextCursor);
    }

    private static ConversationSearchResultItem ToResultItem(SearchResultRow r) => new(
        new ConversationId(r.ConversationId),
        new MessageId(r.MessageId),
        r.Sequence,
        r.MatchedBody,
        Enum.Parse<MessageAuthorKind>(r.AuthorKind),
        new DateTimeOffset(DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc)),
        r.ConversationState);

    private sealed record SearchResultRow(
        Guid MessageId, Guid ConversationId, int Sequence, string MatchedBody, string AuthorKind, DateTime CreatedAt, string ConversationState);
}
