using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetOperatorTeam;

/// <summary>
/// `23-22`: a plain permission check plus a plain read - no lock, no transaction, the same "nothing
/// here for a lock to protect" shape
/// <see cref="Application.UseCases.GetSeatAssignmentSummary.GetSeatAssignmentSummaryHandler"/>'s own
/// remarks already give for a sibling read on the same table.
/// </summary>
public sealed class GetOperatorTeamHandler(IOperatorTeamReadStore team, IPermissionChecker permissions)
{
    public async Task<Result<OperatorTeamResponse>> HandleAsync(GetOperatorTeam query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.SiteManageOperators, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to manage this site's operators.");
        }

        var rows = await team.GetForSiteAsync(query.SiteId, cancellationToken);

        return new OperatorTeamResponse(rows
            .Select(r => new OperatorTeamMemberDto(r.OperatorId.Value, r.DisplayName, r.Email, r.HoldsSeat))
            .ToList());
    }
}
