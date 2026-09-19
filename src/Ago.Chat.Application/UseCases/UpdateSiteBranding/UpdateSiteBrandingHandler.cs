using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.UpdateSiteBranding;

/// <summary>
/// `25-160`: the console's own write for <see cref="Site.BrandCompanyName"/> - "written through the
/// ordinary site-settings path" (this backlog item's own Scope), the identical single-aggregate,
/// unbatched-outbox shape <c>UpdateWidgetConfigHandler</c>'s own remarks describe for itself.
/// </summary>
public sealed class UpdateSiteBrandingHandler(
    ISiteRepository sites, IPermissionChecker permissions, IOutboxWriter outbox, IIdGenerator idGenerator, IClock clock)
{
    /// <summary>A generous ceiling on a company display name shown in one line of an email header - not
    /// measured, a judgement call the same way every other free-text length ceiling in this codebase
    /// states itself to be.</summary>
    public const int MaxLength = 200;

    public async Task<Result<string?>> HandleAsync(UpdateSiteBranding command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to configure this site's branding.");
        }

        if (command.BrandCompanyName is { Length: > MaxLength })
        {
            return ConversationErrors.SiteBrandCompanyNameTooLong(command.BrandCompanyName.Length, MaxLength);
        }

        var site = await sites.GetByIdAsync(command.SiteId, cancellationToken);
        if (site is null)
        {
            return ConversationErrors.SiteNotFound(command.SiteId.Value);
        }

        var now = clock.UtcNow;
        // Blank collapses to null - "no brand name set" and "set to an empty string" are the same fact
        // to every reader of this field (TenantReplyEmailShell falls back to Site.Name for either).
        var normalized = string.IsNullOrWhiteSpace(command.BrandCompanyName) ? null : command.BrandCompanyName;
        site.UpdateBrandCompanyName(normalized, now);

        var changed = site.DomainEvents.OfType<SiteBrandCompanyNameUpdated>().Single();
        outbox.Enqueue(SiteBrandCompanyNameUpdatedMapper.ToEnvelope(changed, idGenerator));
        site.ClearDomainEvents();

        await sites.SaveAsync(site, cancellationToken);

        return normalized;
    }
}
