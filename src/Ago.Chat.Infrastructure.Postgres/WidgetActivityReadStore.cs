using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Dapper;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `23-07`: the read side of the funnel - Dapper over `site_widget_activity`, `adr/0004`'s read-side
/// rule, the identical shape <see cref="ConversationReadStore"/> already takes for an admin-facing
/// aggregate read.
/// </summary>
public sealed class WidgetActivityReadStore(Npgsql.NpgsqlDataSource dataSource) : IWidgetActivityReadStore
{
    public async Task<WidgetActivityTotals> GetTotalsAsync(SiteId siteId, DateOnly since, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        // An aggregate with no `GROUP BY` always returns exactly one row, even over zero matching
        // rows (a `NULL` sum, which `coalesce` turns into 0 before it ever reaches Dapper) - so a
        // brand-new site with no `site_widget_activity` row at all reads back as
        // `WidgetActivityTotals.None`'s own values through this same query, not through a second
        // "no row" branch here.
        var row = await connection.QuerySingleAsync<WidgetActivityTotalsRow>(
            new CommandDefinition(
                """
                select coalesce(sum(loads), 0)::int as "Loads",
                       coalesce(sum(opens), 0)::int as "Opens",
                       coalesce(sum(conversations), 0)::int as "Conversations"
                from site_widget_activity
                where site_id = @SiteId and day >= @Since
                """,
                new { SiteId = siteId.Value, Since = since },
                cancellationToken: cancellationToken));

        return new WidgetActivityTotals(row.Loads, row.Opens, row.Conversations);
    }

    private sealed record WidgetActivityTotalsRow(int Loads, int Opens, int Conversations);
}
