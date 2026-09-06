using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetContactVisibility;

/// <summary>
/// `23-11`: the settings screen's own read of `sites.contact_visibility`, gated on
/// <see cref="Permission.SiteConfigure"/> - the same tenant-level-site-behaviour reasoning
/// `GetAssignmentPenaltyHandler`'s own remarks give for its sibling scalar setting: this is not a new
/// capability that earns its own permission, it is the same class of admin-only configuration
/// `SiteConfigure` already gates.
///
/// <para>Deliberately uncached, matching <c>GetAssignmentPenaltyHandler</c>'s own reasoning: this is
/// a low-frequency admin read (the settings screen a tenant opens rarely), not the per-request path
/// that actually needs the cached, low-latency answer - <c>ListVisitorContactDetailsHandler</c>'s own
/// read of this same fact goes through <c>GetSiteConfigByIdHandler</c>'s cache-aside `SiteConfigDto`
/// instead, because that read happens on every contact-details list a busy operator opens, and this
/// one does not.</para>
/// </summary>
public sealed class GetContactVisibilityHandler(ISiteRepository sites, IPermissionChecker permissions)
{
    public async Task<Result<ContactVisibility>> HandleAsync(
        GetContactVisibility query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden(
                "Operator does not have permission to view this site's contact visibility setting.");
        }

        var site = await sites.GetByIdAsync(query.SiteId, cancellationToken);
        if (site is null)
        {
            return ConversationErrors.SiteNotFound(query.SiteId.Value);
        }

        return site.ContactVisibility;
    }
}
