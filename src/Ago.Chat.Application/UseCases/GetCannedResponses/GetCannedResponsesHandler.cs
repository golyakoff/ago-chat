using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetCannedResponses;

/// <summary>
/// `18-03`: the console's read of a site's canned-response library.
///
/// <para>Gated on <see cref="Permission.SiteConfigure"/> - the same permission
/// `GetOfflineAutoReplyHandler`'s own remarks give for reusing it rather than inventing a new one: this
/// is the same class of tenant-level setting, held by the same "Admin" role.</para>
///
/// <para>Returns the Domain value directly, the same `GetOfflineAutoReplyHandler` precedent - no
/// parallel DTO for a shape the HTTP edge maps on its own (<c>CannedResponseEndpoints</c>).</para>
/// </summary>
public sealed class GetCannedResponsesHandler(ISiteRepository sites, IPermissionChecker permissions)
{
    public async Task<Result<IReadOnlyList<CannedResponse>>> HandleAsync(
        GetCannedResponses query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden(
                "Operator does not have permission to view this site's canned responses.");
        }

        var site = await sites.GetByIdAsync(query.SiteId, cancellationToken);
        if (site is null)
        {
            return ConversationErrors.SiteNotFound(query.SiteId.Value);
        }

        return Result<IReadOnlyList<CannedResponse>>.Success(site.CannedResponses);
    }
}
