using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Dapper;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `18-14`: hand-written SQL over the write model, never through an aggregate (`adr/0004`) - behind
/// <see cref="IModuleFlowReadStore"/>, which carries the full "why this shape, what it does and does
/// not honestly claim" statement. This class only turns that statement into SQL.
///
/// <para><b>Shape of the query, and why it is this shape.</b> <c>module_tasks</c> carries no
/// <c>site_id</c> of its own (`ModuleTaskConfiguration`'s own columns - only <c>conversation_id</c>),
/// so an ordinary inner join to <c>conversations</c> is what scopes the read to one site, the same
/// "join to reach the tenant column" shape every other <c>module_tasks</c>-adjacent read in this
/// codebase would need. One pass, one aggregate row - no <c>GROUPING SETS</c> the way `18-08`'s sibling
/// read needs, because this report has no per-channel/per-operator dimension to split by; it answers
/// exactly one question (started vs. closed) for exactly one module key and one window.</para>
///
/// <para><b><c>count(*) filter (where ...)</c>, not two separate queries or a client-side count.</b>
/// One pass over the matched rows produces both numbers in the same statement, the identical technique
/// <see cref="OperatorAnalyticsReadStore"/>'s own <c>MissedCount</c> column already uses.</para>
///
/// <para><b>A site with no matching tasks in the window returns one row of zeros, not zero rows.</b>
/// Unlike <see cref="OperatorAnalyticsReadStore"/>'s <c>GROUPING SETS</c> query (which genuinely
/// produces zero output rows over zero input rows, forcing that class to substitute an honest zero
/// bucket itself), a bare <c>count(*)</c> with no <c>GROUP BY</c> always returns exactly one row - SQL's
/// own aggregate-over-empty-input behaviour - so there is no equivalent substitution to do here.</para>
/// </summary>
public sealed class ModuleFlowReadStore(NpgsqlDataSource dataSource) : IModuleFlowReadStore
{
    private static readonly string ClosedState = nameof(ModuleTaskState.Closed);

    private const string SiteModuleFlowReportSql = """
        select
            count(*) as "FlowsStarted",
            count(*) filter (where mt.state = @ClosedState) as "FlowsClosed"
        from module_tasks mt
        join conversations c on c.id = mt.conversation_id
        where c.site_id = @SiteId
          and mt.module_key = @ModuleKey
          and mt.opened_at >= @From
          and mt.opened_at < @To
        """;

    public async Task<ModuleFlowReportResult> GetSiteModuleFlowReportAsync(
        SiteId siteId, ModuleKey moduleKey, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var row = await connection.QuerySingleAsync<ModuleFlowReportResult>(new CommandDefinition(
            SiteModuleFlowReportSql,
            new
            {
                SiteId = siteId.Value,
                // `moduleKey.Value` - a runtime string comparison against the caller-supplied value,
                // never a literal in this file (IModuleFlowReadStore's own remarks on guard 9).
                ModuleKey = moduleKey.Value,
                From = from,
                To = to,
                ClosedState,
            },
            cancellationToken: cancellationToken));

        return row;
    }
}
