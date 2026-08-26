using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetOfflineAutoReply;

/// <summary>
/// `14-04`: the console's read of a site's offline auto-reply script.
///
/// <para>Gated on <see cref="Permission.SiteConfigure"/> - the permission `5-08` introduced and
/// `11-01` gave its second caller. The item is explicit that a single boolean does not earn a
/// permission of its own, and it reads correctly here: "configure this site" already covers the
/// widget's appearance and the site-wide conversation view, and whether the site answers visitors
/// automatically is the same class of tenant-level setting, held by the same "Admin" role
/// (`authorization.md`).</para>
///
/// <para>Returns the Domain value object rather than a parallel DTO, unlike its
/// <c>GetWidgetConfigHandler</c> sibling. <see cref="OfflineAutoReplySettings"/> is already exactly
/// the shape this read wants, already validated, and carries no identity or behaviour a caller could
/// misuse - a <c>OfflineAutoReplyDto</c> mirroring it field for field would be a type whose only job
/// is to be copied into. The HTTP edge still does its own mapping (<c>OfflineAutoReplyEndpoints</c>),
/// so the wire shape stays free to differ, which is the part that actually mattered in
/// <c>WidgetConfigDto</c>'s case (a Domain enum that must not leak its storage spelling).</para>
/// </summary>
public sealed class GetOfflineAutoReplyHandler(ISiteRepository sites, IPermissionChecker permissions)
{
    public async Task<Result<OfflineAutoReplySettings>> HandleAsync(
        GetOfflineAutoReply query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden(
                "Operator does not have permission to view this site's offline auto-reply.");
        }

        var site = await sites.GetByIdAsync(query.SiteId, cancellationToken);
        if (site is null)
        {
            return ConversationErrors.SiteNotFound(query.SiteId.Value);
        }

        return site.OfflineAutoReply;
    }
}
