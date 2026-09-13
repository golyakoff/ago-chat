using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetVisitorRestrictionsForSite;

/// <summary>
/// `23-69`'s own Done-when: "the tenant can see how many, by whom, and read the conversations
/// themselves." Gated on <see cref="Permission.SiteConfigure"/>, the same tenant-wide-oversight
/// placement <see cref="Ago.Chat.Application.UseCases.GetAccessRecordsForSite.GetAccessRecordsForSiteHandler"/>
/// already uses for a sibling read - this is cross-operator visibility into every restriction any
/// operator on this site has created, the same "reads across the whole site" shape
/// <c>GetAllConversationsForSiteHandler</c> is gated identically for.
/// </summary>
public sealed class GetVisitorRestrictionsForSiteHandler(IVisitorRestrictionRepository restrictions, IPermissionChecker permissions)
{
    internal const int DefaultLimit = 50;

    internal const int MaxLimit = 200;

    public async Task<Result<VisitorRestrictionPage>> HandleAsync(
        GetVisitorRestrictionsForSite query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to read this site's visitor restrictions.");
        }

        var limit = Math.Clamp(query.Limit ?? DefaultLimit, 1, MaxLimit);

        var page = await restrictions.ListForSiteAsync(query.SiteId, query.Before, limit, cancellationToken);
        return page;
    }
}
