using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetSeatAssignmentSummary;

/// <summary>
/// `13-03`: a plain read across two independent counts (`IOperatorRepository.CountHeldSeatsAsync`,
/// `ISiteRepository.GetByIdAsync`'s own `SeatLimit`) - no lock, no transaction. The over-seats condition
/// is exactly as fresh as the moment each of those two reads ran, which is the correct answer for a
/// derived, read-time condition (this item's own Scope): a downgrade committing between the two reads
/// changes what the *next* call to this handler reports, never what this one reports, and Postgres MVCC
/// guarantees each individual read is internally consistent - there is nothing here for a lock to
/// protect that a lock would not also have to hold across an entire console page render.
/// </summary>
public sealed class GetSeatAssignmentSummaryHandler(
    IOperatorRepository operators, ISiteRepository sites, IPermissionChecker permissions)
{
    public async Task<Result<SeatAssignmentSummaryDto>> HandleAsync(
        GetSeatAssignmentSummary query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.SiteManageOperators, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to manage this site's operators.");
        }

        var site = await sites.GetByIdAsync(query.SiteId, cancellationToken);
        if (site is null)
        {
            return ConversationErrors.SiteNotFound(query.SiteId.Value);
        }

        var held = await operators.CountHeldSeatsAsync(query.SiteId, cancellationToken);

        return new SeatAssignmentSummaryDto(held, site.SeatLimit, held > site.SeatLimit);
    }
}
