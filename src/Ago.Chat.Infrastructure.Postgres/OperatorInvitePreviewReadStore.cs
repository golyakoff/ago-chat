using Ago.Chat.Application.Abstractions;
using Dapper;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `23-70`: hand-written SQL over the write model, never through the <c>OperatorInvite</c> aggregate
/// (`adr/0004`) - see <see cref="IOperatorInvitePreviewReadStore"/>'s own remarks for why this is its
/// own port rather than a fourth thing <see cref="OperatorInviteRepository"/> or
/// <see cref="OperatorInviteRedemptionRepository"/> answer. `ux_operator_invites_code_hash`
/// (`OperatorInviteConfiguration`) is what makes this a single indexed lookup, the same unique index
/// every redemption already reads through.
///
/// <para>Two <c>left join</c>s, not inner: a site row and the inviting operator's row are both
/// guaranteed to exist by foreign keys today, but `left join` costs nothing here and means this query
/// degrades to a still-useful (if name-less) preview rather than vanishing outright if either
/// assumption is ever relaxed - the identical defensive shape <see cref="OperatorTeamReadStore"/>'s own
/// nullable <c>DisplayName</c>/<c>Email</c> columns already assume for the second join.</para>
/// </summary>
public sealed class OperatorInvitePreviewReadStore(NpgsqlDataSource dataSource) : IOperatorInvitePreviewReadStore
{
    private const string Sql = """
        select s.name as "SiteName", creator.display_name as "InvitedByDisplayName",
               oi.expires_at as "ExpiresAt", (oi.redeemed_at is not null) as "IsRedeemed"
        from operator_invites oi
        left join sites s on s.id = oi.site_id
        left join operators creator on creator.id = oi.created_by_operator_id
        where oi.code_hash = @CodeHash
        """;

    public async Task<OperatorInvitePreviewItem?> GetByCodeHashAsync(byte[] codeHash, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var row = await connection.QuerySingleOrDefaultAsync<OperatorInvitePreviewRow>(new CommandDefinition(
            Sql, new { CodeHash = codeHash }, cancellationToken: cancellationToken));

        return row is null
            ? null
            // `s.name` can only be null if the left join found no site, which a foreign key rules out
            // in practice (`OperatorInviteRedemptionRepository.LockSiteAndReadSeatLimitAsync`'s own
            // remarks on that same guarantee) - coalesced to "" rather than surfaced as a second
            // nullable field this read store's one caller would have to handle for a case that cannot
            // occur.
            : new OperatorInvitePreviewItem(
                row.SiteName ?? "",
                row.InvitedByDisplayName,
                new DateTimeOffset(DateTime.SpecifyKind(row.ExpiresAt, DateTimeKind.Utc)),
                row.IsRedeemed);
    }

    // `DateTime`, not `DateTimeOffset` - Dapper's positional-record materialization requires an exact
    // constructor-parameter-type match against what Npgsql hands back for `timestamptz`
    // (`System.DateTime`, found live: this store 500'd on its first real Postgres run with "no
    // constructor... matching signature (String, String, DateTime, Boolean)" until this split matched
    // it). `ChannelDeliveryReadStore`/`WebhookDeliveryReadStore` already establish the fix - the raw row
    // stays `DateTime`, and `GetByCodeHashAsync` above does the one conversion to `DateTimeOffset`
    // (`DateTimeKind.Utc`, since `timestamptz` is always UTC at rest) before handing the result to a
    // caller, so the port's own contract - `OperatorInvitePreviewItem.ExpiresAt` - stays the
    // `DateTimeOffset` `adr/0011` requires everywhere outside this one Dapper materialization detail.
    private sealed record OperatorInvitePreviewRow(string? SiteName, string? InvitedByDisplayName, DateTime ExpiresAt, bool IsRedeemed);
}
