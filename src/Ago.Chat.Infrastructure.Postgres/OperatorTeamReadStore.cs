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
/// <c>OperatorInviteRedemptionRepository</c>'s own seat-limit count already reads through - no new index
/// needed for a query filtering on exactly the pair that one already covers.
///
/// <para><b>`25-170`: one row per <c>(operator, role)</c> pairing, aggregated back into one row per
/// operator with a nested role list.</b> "Holds a seat" moved off `operators` onto `operator_roles`, so
/// this query now reads `operator_roles.holds_seat` per role rather than the single, removed
/// `operators.holds_seat` column - <see cref="OperatorRoleSeatAssignment"/>'s own remarks explain why a
/// flat boolean could never have expressed a founder holding one role's seat and not the other's.</para>
/// </summary>
public sealed class OperatorTeamReadStore(NpgsqlDataSource dataSource) : IOperatorTeamReadStore
{
    // `23-72`/`25-170`: left-joined, not inner-joined - an operator row with no operator_roles entry at
    // all (it should not exist in practice, but this read has no reason to silently drop such a row from
    // the team list the way an inner join would) still comes back with an empty role list rather than
    // vanishing from the page. One row per (operator, role) pairing here - aggregated in code, not SQL
    // (this class's own remarks on why: array_agg over two correlated columns (name, holds_seat) needs
    // either two parallel arrays or a composite type, and Dapper's own multi-mapping is simpler to read
    // than either for a handful of rows per site).
    private const string Sql = """
        select o.id as "OperatorId", o.display_name as "DisplayName", o.email as "Email",
               r.name as "RoleName", orl.holds_seat as "HoldsSeat"
        from operators o
        left join operator_roles orl on orl.operator_id = o.id
        left join roles r on r.id = orl.role_id
        where o.site_id = @SiteId and o.removed_at is null
        order by o.display_name nulls last, o.id
        """;

    public async Task<IReadOnlyList<OperatorTeamMemberItem>> GetForSiteAsync(SiteId siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var rows = await connection.QueryAsync<OperatorTeamRow>(new CommandDefinition(
            Sql, new { SiteId = siteId.Value }, cancellationToken: cancellationToken));

        // Grouped in code, preserving the SQL's own display-name order - one operator row can arrive as
        // several (operator, role) rows (the founder's own two seeded roles), or, for a roleless operator
        // (should not exist in practice), one row with a null RoleName that must not become a phantom
        // role assignment.
        return rows
            .GroupBy(r => (r.OperatorId, r.DisplayName, r.Email))
            .Select(g => new OperatorTeamMemberItem(
                new OperatorId(g.Key.OperatorId), g.Key.DisplayName, g.Key.Email,
                g.Where(r => r.RoleName is not null)
                    .Select(r => new OperatorRoleSeatAssignment(r.RoleName!, r.HoldsSeat))
                    .ToList()))
            .ToList();
    }

    private sealed class OperatorTeamRow
    {
        public Guid OperatorId { get; init; }
        public string? DisplayName { get; init; }
        public string? Email { get; init; }
        public string? RoleName { get; init; }
        public bool HoldsSeat { get; init; }
    }
}
