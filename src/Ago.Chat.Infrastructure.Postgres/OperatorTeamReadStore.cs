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
    // `23-72`: left-joined, not inner-joined - an operator row with no operator_roles entry at all (it
    // should not exist in practice, but this read has no reason to silently drop such a row from the
    // team list the way an inner join would) still comes back with an empty RoleNames rather than
    // vanishing from the page. array_agg over a left join produces one all-NULL array element for a
    // roleless operator; the FILTER clause keeps that element out rather than returning `{NULL}`.
    private const string Sql = """
        select o.id as "OperatorId", o.display_name as "DisplayName", o.email as "Email", o.holds_seat as "HoldsSeat",
               coalesce(array_agg(r.name) filter (where r.name is not null), array[]::text[]) as "RoleNames"
        from operators o
        left join operator_roles orl on orl.operator_id = o.id
        left join roles r on r.id = orl.role_id
        where o.site_id = @SiteId and o.removed_at is null
        group by o.id, o.display_name, o.email, o.holds_seat
        order by o.display_name nulls last, o.id
        """;

    public async Task<IReadOnlyList<OperatorTeamMemberItem>> GetForSiteAsync(SiteId siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var rows = await connection.QueryAsync<OperatorTeamRow>(new CommandDefinition(
            Sql, new { SiteId = siteId.Value }, cancellationToken: cancellationToken));

        return rows
            .Select(r => new OperatorTeamMemberItem(new OperatorId(r.OperatorId), r.DisplayName, r.Email, r.HoldsSeat, r.RoleNames))
            .ToList();
    }

    // `23-72`: a plain class with a parameterless constructor, not the positional-record shape this
    // type had before - Dapper's constructor-matching materialiser cannot resolve a `text[]` column
    // against a `string[]` constructor parameter ("System.Array RoleNames" in its own error), found
    // running this change's own new integration tests. Property-setting (Dapper's other, older
    // materialisation path, used whenever no matching constructor is found) has no such limitation.
    private sealed class OperatorTeamRow
    {
        public Guid OperatorId { get; init; }
        public string? DisplayName { get; init; }
        public string? Email { get; init; }
        public bool HoldsSeat { get; init; }
        public string[] RoleNames { get; init; } = [];
    }
}
