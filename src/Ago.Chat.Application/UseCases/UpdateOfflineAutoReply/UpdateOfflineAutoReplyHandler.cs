using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.UpdateOfflineAutoReply;

/// <summary>
/// `14-04`: the console's write, and the second real producer of <c>SiteSettingsChanged</c> after
/// `11-01`'s widget-config write.
///
/// <para>Same `site:configure` gate as the read beside it, same reasoning
/// (<c>GetOfflineAutoReplyHandler</c>'s remarks) - no new permission for a boolean, exactly as the
/// backlog item instructs.</para>
///
/// <para>Validation happens here, in Application: the value objects still throw defensively whatever
/// called them, but this handler is what turns an expected bad input into a <c>Result</c> failure -
/// `11-01`'s precedent, and <c>CreateAttachmentHandler</c>'s before it. That is also why the raw
/// strings travel this far rather than being validated at the HTTP edge: the edge's job is
/// deserialisation, not knowing what a legal rule is.</para>
///
/// <para><b>The outbox row is what makes the toggle live config.</b> The state change and its
/// <c>SiteSettingsChanged</c> event commit in one transaction (`adr/0005`); the event reaches
/// <c>SiteCacheInvalidationConsumer</c>, which drops this site's cached config on every node under
/// both of its keys. Without that, flipping the toggle would take effect only when a five-minute TTL
/// happened to expire - which would still be "no rebuild", but not the "changes live behaviour"
/// the item's Done-when actually asks for.</para>
/// </summary>
public sealed class UpdateOfflineAutoReplyHandler(
    ISiteRepository sites,
    IPermissionChecker permissions,
    IOutboxWriter outbox,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<OfflineAutoReplySettings>> HandleAsync(
        UpdateOfflineAutoReply command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden(
                "Operator does not have permission to configure this site's offline auto-reply.");
        }

        OfflineAutoReplySettings settings;
        try
        {
            var rules = command.Rules
                .Select(rule => new OfflineAutoReplyRule(rule.Keyword, rule.Reply))
                .ToList();
            settings = new OfflineAutoReplySettings(command.Enabled, command.FallbackReply.Trim(), rules);
        }
        catch (ArgumentException ex)
        {
            return ConversationErrors.OfflineAutoReplyInvalid(ex.Message);
        }

        var site = await sites.GetByIdAsync(command.SiteId, cancellationToken);
        if (site is null)
        {
            return ConversationErrors.SiteNotFound(command.SiteId.Value);
        }

        site.UpdateOfflineAutoReply(settings, clock.UtcNow);

        var domainEvent = site.DomainEvents.OfType<SiteOfflineAutoReplyUpdated>().Single();
        outbox.Enqueue(SiteOfflineAutoReplyUpdatedMapper.ToEnvelope(domainEvent, idGenerator));
        site.ClearDomainEvents();

        await sites.SaveAsync(site, cancellationToken);

        return settings;
    }
}
