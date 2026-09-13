using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.ListOperatorInvites;

/// <summary>
/// `25-73`: gated by the identical <see cref="Permission.SiteManageOperators"/>
/// <see cref="CreateOperatorInvite.CreateOperatorInviteHandler"/> already checks - whoever may invite a
/// colleague may also see who they have already invited and revoke one, the same permission boundary
/// `OperatorsTeamPage`'s own dedicated `site:manage_operators` check already draws for the operator
/// table this screen's new list renders beneath.
/// </summary>
public sealed class ListOperatorInvitesHandler(
    IOperatorInviteListReadStore invites, IPermissionChecker permissions, IClock clock)
{
    public async Task<Result<IReadOnlyList<OperatorInviteListEntry>>> HandleAsync(
        ListOperatorInvites query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.SiteManageOperators, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to manage operators for this site.");
        }

        var rows = await invites.ListForSiteAsync(query.SiteId, cancellationToken);
        var now = clock.UtcNow;

        var entries = new List<OperatorInviteListEntry>(rows.Count);
        entries.AddRange(rows.Select(row => new OperatorInviteListEntry(
            row.Id.Value, row.Email, row.CreatedAt, row.ExpiresAt, StatusOf(row, now), row.SendFailureCode)));

        return entries;
    }

    // Revoked/redeemed are both permanent, terminal outcomes and outrank a merely-expired clock
    // reading or a send failure that happened along the way - see ListOperatorInvites.cs's own remarks
    // on OperatorInviteListStatus for the full ordering reasoning.
    private static OperatorInviteListStatus StatusOf(OperatorInviteListItem row, DateTimeOffset now) =>
        row.RevokedAt is not null ? OperatorInviteListStatus.Revoked
        : row.RedeemedAt is not null ? OperatorInviteListStatus.Redeemed
        : row.SendFailureCode is not null ? OperatorInviteListStatus.SendFailed
        : now >= row.ExpiresAt ? OperatorInviteListStatus.Expired
        : OperatorInviteListStatus.Sent;
}
