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
            row.Id.Value, row.Email, row.CreatedAt, row.ExpiresAt, StatusOf(row, now), row.SendFailureCode, row.RoleNames,
            EffectiveStatusOf(row, now), row.RedeemedAt, row.RevokedAt, RemovedAtOf(row))));

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

    // `26-263`: the effective team-membership status - see OperatorInviteEffectiveStatus's own remarks for
    // the priority reasoning. Revoked is terminal and outranks a clock-based Expired; Expired applies only
    // to an invite never redeemed and now past its expiry; a redeemed invite is InTeam while its operator
    // is still present and active (`removed_at IS NULL`) and Removed once that operator has been
    // soft-removed - a redeemed invite whose operator row is somehow gone (RedeemedByOperatorId null)
    // reads as Removed rather than pretending the person is still in the team.
    private static OperatorInviteEffectiveStatus EffectiveStatusOf(OperatorInviteListItem row, DateTimeOffset now)
    {
        if (row.RevokedAt is not null)
        {
            return OperatorInviteEffectiveStatus.Revoked;
        }

        if (row.RedeemedAt is not null)
        {
            return row.RedeemedByOperatorId is not null && row.RedeemedOperatorRemovedAt is null
                ? OperatorInviteEffectiveStatus.InTeam
                : OperatorInviteEffectiveStatus.Removed;
        }

        return now >= row.ExpiresAt
            ? OperatorInviteEffectiveStatus.Expired
            : OperatorInviteEffectiveStatus.Pending;
    }

    // The redeemed operator's own removal instant - surfaced only when the effective status is actually
    // Removed, so a still-active or never-redeemed invite carries a null «Удалено» line rather than a
    // stray date.
    private static DateTimeOffset? RemovedAtOf(OperatorInviteListItem row) =>
        row.RedeemedAt is not null && row.RevokedAt is null ? row.RedeemedOperatorRemovedAt : null;
}
