using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Dapper;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `25-73`: hand-written SQL over the write model, never through the <see cref="OperatorInvite"/>
/// aggregate (`adr/0004`) - the identical split <see cref="OperatorInvitePreviewReadStore"/> already
/// draws for the same reason: this is its own port rather than a fourth thing
/// <see cref="OperatorInviteRepository"/> or <see cref="OperatorInviteRedemptionRepository"/> answer.
/// One indexed, per-site scan (`ix_operator_invites_site_id` - EF Core already indexes this FK by
/// convention; `OperatorInviteConfiguration` just names it explicitly) ordered newest-first, matching
/// the console's own table.
/// </summary>
public sealed class OperatorInviteListReadStore(NpgsqlDataSource dataSource) : IOperatorInviteListReadStore
{
    // `26-258`: the role SET each invite grants, joined back through `operator_invite_roles` to the
    // `roles` catalogue (`26-241` stored the ids; this read turns them into the names the console shows).
    // A `left join` so an invite with no role rows still returns one list row; `array_agg(... order by
    // r.name)` makes the set deterministic and `array_remove(..., null)` collapses the single-null array
    // a match-less left join produces into an empty `text[]` (Npgsql maps that straight to `string[]`),
    // never a null the caller would have to guard. One `group by` per invite keeps this the same single
    // indexed per-site scan it was before, now with the aggregate folded in.
    private const string Sql = """
        select i.id as "Id", i.email as "Email", i.created_at as "CreatedAt", i.expires_at as "ExpiresAt",
               i.redeemed_at as "RedeemedAt", i.revoked_at as "RevokedAt", i.send_failure_code as "SendFailureCode",
               array_remove(array_agg(r.name order by r.name), null) as "RoleNames"
        from operator_invites i
        left join operator_invite_roles ir on ir.operator_invite_id = i.id
        left join roles r on r.id = ir.role_id
        where i.site_id = @SiteId
        group by i.id
        order by i.created_at desc
        """;

    public async Task<IReadOnlyList<OperatorInviteListItem>> ListForSiteAsync(SiteId siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var rows = await connection.QueryAsync<OperatorInviteListRow>(new CommandDefinition(
            Sql, new { SiteId = siteId.Value }, cancellationToken: cancellationToken));

        // `DateTime`, not `DateTimeOffset` - Dapper materialization requires an exact type match against
        // what Npgsql hands back for `timestamptz`, the identical fix `OperatorInvitePreviewReadStore`'s
        // own remarks already document (found live on that store's own first real Postgres run). The raw
        // row stays `DateTime`; this method does the one conversion to `DateTimeOffset` per instant
        // before handing results to a caller.
        return [.. rows.Select(row => new OperatorInviteListItem(
            new OperatorInviteId(row.Id),
            row.Email,
            AsUtc(row.CreatedAt),
            AsUtc(row.ExpiresAt),
            row.RedeemedAt is { } redeemedAt ? AsUtc(redeemedAt) : null,
            row.RevokedAt is { } revokedAt ? AsUtc(revokedAt) : null,
            row.SendFailureCode,
            row.RoleNames))];
    }

    private static DateTimeOffset AsUtc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    // A settable-property class, not the positional record the rest of this store's scalars would allow -
    // Npgsql describes the computed `array_remove(array_agg(...))` column's type as the base `System.Array`
    // (its element type is not resolved for an aggregate expression the way a declared `text[]` column's
    // is), and Dapper's positional-constructor matching demands the parameter type equal that described
    // type exactly, so a `string[]` constructor parameter is refused outright. Dapper's by-name property
    // path instead assigns the runtime value (a real `string[]` for a `text[]` result) straight into the
    // property, which is why `RoleNames` binds here where it would not in a primary constructor.
    private sealed class OperatorInviteListRow
    {
        public Guid Id { get; init; }
        public string Email { get; init; } = string.Empty;
        public DateTime CreatedAt { get; init; }
        public DateTime ExpiresAt { get; init; }
        public DateTime? RedeemedAt { get; init; }
        public DateTime? RevokedAt { get; init; }
        public string? SendFailureCode { get; init; }
        public string[] RoleNames { get; init; } = [];
    }
}
