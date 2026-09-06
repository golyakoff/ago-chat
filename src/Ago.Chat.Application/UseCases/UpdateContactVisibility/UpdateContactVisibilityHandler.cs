using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.UpdateContactVisibility;

/// <summary>
/// `23-11`/`decisions.md` §5's amendment: the console's write for `sites.contact_visibility` - "the
/// rung is one setting on the tenant... no shared type crosses the product boundary; a shared *rule*
/// does." Same `site:configure` gate as <see cref="GetContactVisibility.GetContactVisibilityHandler"/>,
/// same reasoning - no new permission for one enum column.
///
/// <para><b>Rejects rung three by construction, not by a denylist.</b> <c>Enum.TryParse</c> plus
/// <c>Enum.IsDefined</c> against <see cref="ContactVisibility"/> is the entire guard - "Never" is not
/// checked for and refused, it simply is not a name <c>Enum.TryParse</c> can ever produce, because the
/// enum this parses against has no such member (<see cref="ContactVisibility"/>'s own remarks: absent
/// from the type, not filtered out of it). A request naming it gets the identical
/// <see cref="ConversationErrors.ContactVisibilityInvalidRung"/> any other unrecognised string would.</para>
///
/// <para><b>One domain write, two outbox rows, one transaction.</b> <see cref="Site.UpdateContactVisibility"/>
/// raises exactly one domain event, <see cref="Domain.SiteContactVisibilityUpdated"/>, and this handler
/// maps it twice - <see cref="SiteContactVisibilityUpdatedMapper"/> (chat's own
/// <c>SiteSettingsChanged</c> cache-invalidation contract) and <see cref="ContactVisibilityChangedMapper"/>
/// (the cross-boundary <c>ContactVisibilityChanged</c> contract `23-12`'s calendar-side consumer
/// reads) - both enqueued before the one <c>SaveChangesAsync</c> this write makes (`CLAUDE.md` rule 4:
/// the state change and every integration event it produces commit together). See
/// <see cref="Domain.SiteContactVisibilityUpdated"/>'s own remarks for why one fact has two honest
/// audiences rather than being folded into either contract alone.</para>
/// </summary>
public sealed class UpdateContactVisibilityHandler(
    ISiteRepository sites,
    IPermissionChecker permissions,
    IOutboxWriter outbox,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<ContactVisibility>> HandleAsync(
        UpdateContactVisibility command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden(
                "Operator does not have permission to configure this site's contact visibility.");
        }

        if (!Enum.TryParse<ContactVisibility>(command.Rung, ignoreCase: true, out var rung) || !Enum.IsDefined(rung))
        {
            return ConversationErrors.ContactVisibilityInvalidRung(
                $"'{command.Rung}' is not a valid contact visibility rung - expected '{nameof(ContactVisibility.Visible)}' "
                + $"or '{nameof(ContactVisibility.MaskedWithReveal)}'.");
        }

        var site = await sites.GetByIdAsync(command.SiteId, cancellationToken);
        if (site is null)
        {
            return ConversationErrors.SiteNotFound(command.SiteId.Value);
        }

        site.UpdateContactVisibility(rung, clock.UtcNow);

        var domainEvent = site.DomainEvents.OfType<SiteContactVisibilityUpdated>().Single();
        outbox.Enqueue(SiteContactVisibilityUpdatedMapper.ToEnvelope(domainEvent, idGenerator));
        outbox.Enqueue(ContactVisibilityChangedMapper.ToEnvelope(domainEvent, idGenerator));
        site.ClearDomainEvents();

        await sites.SaveAsync(site, cancellationToken);

        return site.ContactVisibility;
    }
}
