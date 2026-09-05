using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetAssignmentPenalty;

/// <summary>
/// `23-05`: the console's read of a site's assignment penalty - the settings-screen half of the
/// item, gated on <see cref="Permission.SiteConfigure"/> for the identical reason
/// <c>GetOfflineAutoReplyHandler</c>'s own remarks give: this is the same class of tenant-level site
/// behaviour already held by the same permission, not a new capability that earns one of its own.
///
/// <para>Returns the plain <see langword="int"/> rather than a parallel DTO - <c>Site.AssignmentPenaltySeconds</c>
/// is already exactly the shape this read wants, the same "no DTO whose only job is to be copied into"
/// reasoning <c>GetOfflineAutoReplyHandler</c>'s own remarks give for returning
/// <c>OfflineAutoReplySettings</c> directly.</para>
/// </summary>
public sealed class GetAssignmentPenaltyHandler(ISiteRepository sites, IPermissionChecker permissions)
{
    public async Task<Result<int>> HandleAsync(GetAssignmentPenalty query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden(
                "Operator does not have permission to view this site's assignment penalty.");
        }

        var site = await sites.GetByIdAsync(query.SiteId, cancellationToken);
        if (site is null)
        {
            return ConversationErrors.SiteNotFound(query.SiteId.Value);
        }

        return site.AssignmentPenaltySeconds;
    }
}
