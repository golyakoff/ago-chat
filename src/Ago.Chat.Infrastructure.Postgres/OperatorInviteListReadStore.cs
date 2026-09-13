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
    private const string Sql = """
        select id as "Id", email as "Email", created_at as "CreatedAt", expires_at as "ExpiresAt",
               redeemed_at as "RedeemedAt", revoked_at as "RevokedAt", send_failure_code as "SendFailureCode"
        from operator_invites
        where site_id = @SiteId
        order by created_at desc
        """;

    public async Task<IReadOnlyList<OperatorInviteListItem>> ListForSiteAsync(SiteId siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var rows = await connection.QueryAsync<OperatorInviteListRow>(new CommandDefinition(
            Sql, new { SiteId = siteId.Value }, cancellationToken: cancellationToken));

        // `DateTime`, not `DateTimeOffset` - Dapper's positional-record materialization requires an
        // exact constructor-parameter-type match against what Npgsql hands back for `timestamptz`, the
        // identical fix `OperatorInvitePreviewReadStore`'s own remarks already document (found live on
        // that store's own first real Postgres run). The raw row stays `DateTime`; this method does the
        // one conversion to `DateTimeOffset` per instant before handing results to a caller.
        return [.. rows.Select(row => new OperatorInviteListItem(
            new OperatorInviteId(row.Id),
            row.Email,
            AsUtc(row.CreatedAt),
            AsUtc(row.ExpiresAt),
            row.RedeemedAt is { } redeemedAt ? AsUtc(redeemedAt) : null,
            row.RevokedAt is { } revokedAt ? AsUtc(revokedAt) : null,
            row.SendFailureCode))];
    }

    private static DateTimeOffset AsUtc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed record OperatorInviteListRow(
        Guid Id, string Email, DateTime CreatedAt, DateTime ExpiresAt,
        DateTime? RedeemedAt, DateTime? RevokedAt, string? SendFailureCode);
}
