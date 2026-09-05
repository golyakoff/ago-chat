using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Dapper;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `23-22`: hand-written SQL over the write model, never through the <see cref="Operator"/> aggregate
/// (`adr/0004`) - see <see cref="IOperatorTeamReadStore"/>'s own remarks for why this display read gets
/// its own port instead of a fourth method on <see cref="IOperatorRepository"/>. The `WHERE` clause
/// reuses <c>ix_operators_site_id_removed_at</c> (`OperatorConfiguration`), the same index
/// <c>OperatorInviteRedemptionRepository</c>'s own seat-limit count and
/// <see cref="GetSeatAssignmentSummary.GetSeatAssignmentSummaryHandler"/>'s <c>CountHeldSeatsAsync</c>
/// already read through - no new index needed for a query filtering on exactly the pair that one
/// already covers.
/// </summary>
public sealed class OperatorTeamReadStore(NpgsqlDataSource dataSource) : IOperatorTeamReadStore
{
    private const string Sql = """
        select id as "OperatorId", display_name as "DisplayName", email as "Email", holds_seat as "HoldsSeat"
        from operators
        where site_id = @SiteId and removed_at is null
        order by display_name nulls last, id
        """;

    public async Task<IReadOnlyList<OperatorTeamMemberItem>> GetForSiteAsync(SiteId siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var rows = await connection.QueryAsync<OperatorTeamRow>(new CommandDefinition(
            Sql, new { SiteId = siteId.Value }, cancellationToken: cancellationToken));

        return rows
            .Select(r => new OperatorTeamMemberItem(new OperatorId(r.OperatorId), r.DisplayName, r.Email, r.HoldsSeat))
            .ToList();
    }

    private sealed record OperatorTeamRow(Guid OperatorId, string? DisplayName, string? Email, bool HoldsSeat);
}
