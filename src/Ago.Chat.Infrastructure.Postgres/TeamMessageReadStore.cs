using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Dapper;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>Hand-written SQL over the write model, never through <see cref="TeamMessage"/>
/// (adr/0004) - the team-chat sibling of <see cref="ConversationReadStore"/>, over a plain,
/// non-partitioned table (<see cref="Persistence.TeamMessageConfiguration"/>'s own remarks on why),
/// so unlike that class this one needs no <c>site_id</c>-for-pruning story beyond the ordinary
/// "every query is scoped to one tenant" rule every read store in this product already follows.</summary>
public sealed class TeamMessageReadStore(NpgsqlDataSource dataSource) : ITeamMessageReadStore
{
    // `left join`, not inner - the identical choice ConversationReadStore.AllForSiteSql makes for
    // conversations.operator_id: an author whose operator row has since been removed (RemoveOperatorHandler
    // soft-deletes, never rows the row away) still has a name to show, but a defensive left join costs
    // nothing to keep even though today's soft-delete means the row is always still there.
    private const string HistorySql = """
        select tm.id as "Id", tm.sequence as "Sequence", tm.author_operator_id as "AuthorOperatorId",
               o.display_name as "AuthorDisplayName", o.email as "AuthorEmail",
               tm.author_is_admin as "AuthorIsAdmin", tm.body as "Body", tm.created_at as "CreatedAt",
               tm.client_message_id as "ClientMessageId"
        from team_messages tm
        left join operators o on o.id = tm.author_operator_id
        where tm.site_id = @SiteId
          and (@BeforeSequence is null or tm.sequence < @BeforeSequence)
        order by tm.sequence desc
        limit @PageSize
        """;

    // `3-03`: forward, unbounded, no LIMIT - the same reconnect-delta shape ConversationReadStore.DeltaSql
    // already uses, for the identical reason (ITeamMessageReadStore.GetDeltaAsync's own remarks).
    private const string DeltaSql = """
        select tm.id as "Id", tm.sequence as "Sequence", tm.author_operator_id as "AuthorOperatorId",
               o.display_name as "AuthorDisplayName", o.email as "AuthorEmail",
               tm.author_is_admin as "AuthorIsAdmin", tm.body as "Body", tm.created_at as "CreatedAt",
               tm.client_message_id as "ClientMessageId"
        from team_messages tm
        left join operators o on o.id = tm.author_operator_id
        where tm.site_id = @SiteId and tm.sequence > @AfterSequence
        order by tm.sequence asc
        """;

    private const string BySequenceSql = """
        select tm.id as "Id", tm.sequence as "Sequence", tm.author_operator_id as "AuthorOperatorId",
               o.display_name as "AuthorDisplayName", o.email as "AuthorEmail",
               tm.author_is_admin as "AuthorIsAdmin", tm.body as "Body", tm.created_at as "CreatedAt",
               tm.client_message_id as "ClientMessageId"
        from team_messages tm
        left join operators o on o.id = tm.author_operator_id
        where tm.site_id = @SiteId and tm.sequence = @Sequence
        """;

    public async Task<TeamMessageHistoryPage> GetHistoryAsync(
        SiteId siteId, int? beforeSequence, int pageSize, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<TeamMessageRow>(new CommandDefinition(
            HistorySql, new { SiteId = siteId.Value, BeforeSequence = beforeSequence, PageSize = pageSize },
            cancellationToken: cancellationToken));

        var items = rows.Select(ToItem).ToList();
        var nextCursor = items.Count == pageSize ? items[^1].Sequence : (int?)null;
        return new TeamMessageHistoryPage(items, nextCursor);
    }

    public async Task<IReadOnlyList<TeamMessageHistoryItem>> GetDeltaAsync(
        SiteId siteId, int afterSequence, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<TeamMessageRow>(new CommandDefinition(
            DeltaSql, new { SiteId = siteId.Value, AfterSequence = afterSequence }, cancellationToken: cancellationToken));

        return rows.Select(ToItem).ToList();
    }

    public async Task<TeamMessageHistoryItem?> GetBySequenceAsync(SiteId siteId, int sequence, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<TeamMessageRow>(new CommandDefinition(
            BySequenceSql, new { SiteId = siteId.Value, Sequence = sequence }, cancellationToken: cancellationToken));

        return row is null ? null : ToItem(row);
    }

    private static TeamMessageHistoryItem ToItem(TeamMessageRow row) => new(
        new TeamMessageId(row.Id), row.Sequence, new OperatorId(row.AuthorOperatorId), row.AuthorDisplayName,
        row.AuthorEmail, row.AuthorIsAdmin, row.Body,
        new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc)), row.ClientMessageId);
}
