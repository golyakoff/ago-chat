using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetAccessRecordsForSite;

/// <summary>
/// `24-12`'s own Scope: "reachable by the tenant for their own site, not only by AGO." An ordinary
/// `IPermissionChecker` gate, unlike every platform-owner handler beside it in this stage - this
/// caller genuinely is an operator acting on their own site's own row set, so `adr/0016`'s RBAC model
/// is the correct check to make, not a second, weaker copy of a policy decided elsewhere the way it
/// would be for `ListSitesForOwnerHandler`/`GetSiteForOwnerHandler`. Gated on
/// <see cref="Permission.AccessRecordRead"/>, a dedicated permission rather than a reuse of
/// <see cref="Permission.SiteConfigure"/> - see that permission's own remarks for the blast-radius
/// argument.
///
/// <para><b>Deliberately answers "who accessed this site's data" including AGO's own platform-owner
/// accesses of it</b> - `24-12`'s own open question. <see cref="IAccessRecordRepository.ListForSiteAsync"/>
/// filters only by <c>site_id</c>, with no filter on <c>actor_kind</c>, so a row this same stage wrote
/// for <see cref="AccessRecordKind.OwnerSiteDetail"/> against this tenant's site comes back to this
/// tenant's own Admin exactly like an operator's own <see cref="AccessRecordKind.CrossConversationHistoryRead"/>
/// row would. Withholding AGO's own accesses from the one report built to answer "who read my data"
/// would make this item's own Goal false for the single case a tenant most wants it true for.</para>
/// </summary>
public sealed class GetAccessRecordsForSiteHandler(IAccessRecordRepository accessRecords, IPermissionChecker permissions)
{
    internal const int DefaultLimit = 50;

    internal const int MaxLimit = 200;

    public async Task<Result<AccessRecordPage>> HandleAsync(
        GetAccessRecordsForSite query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.AccessRecordRead, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to read this site's access records.");
        }

        var limit = Math.Clamp(query.Limit ?? DefaultLimit, 1, MaxLimit);

        var page = await accessRecords.ListForSiteAsync(query.SiteId, query.Before, limit, cancellationToken);
        return page;
    }
}
