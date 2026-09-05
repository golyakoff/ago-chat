using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.UpdateAssignmentPenalty;

/// <summary>
/// `23-05`: the console's write for `sites.assignment_penalty_seconds` - the site-configuration
/// control the item's own Scope requires, "on the settings screen that already owns site behaviour."
///
/// <para>Same `site:configure` gate as the read beside it, same reasoning
/// (<c>GetAssignmentPenaltyHandler</c>'s own remarks) - no new permission for one integer.</para>
///
/// <para>Validation happens here, in Application, not at the HTTP edge and not left to
/// <c>Site.UpdateAssignmentPenalty</c>'s own defensive throw - the same "the edge's job is
/// deserialisation, not knowing what a legal value is" split `UpdateOfflineAutoReplyHandler`'s own
/// remarks draw.</para>
///
/// <para><b>The outbox row is what makes the change live config for a console reader</b> -
/// <c>SiteAssignmentPenaltyUpdated</c>/<c>SiteAssignmentPenaltyUpdatedMapper</c>'s own remarks explain
/// why this still emits <c>SiteSettingsChanged</c> even though the one write path that actually reads
/// this column - the two <c>IAssignmentClaimer</c> implementations - never goes through the cache this
/// event evicts. Consistency of the write path matters here more than any real consumer of the
/// eviction: every other <c>Site</c> settings write raises exactly one <c>SiteSettingsChanged</c>-mapped
/// event, and a silent exception for this one field would be a harder fact for a future reader to
/// notice than an event with no consumer that needs it.</para>
/// </summary>
public sealed class UpdateAssignmentPenaltyHandler(
    ISiteRepository sites,
    IPermissionChecker permissions,
    IOutboxWriter outbox,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<int>> HandleAsync(UpdateAssignmentPenalty command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden(
                "Operator does not have permission to configure this site's assignment penalty.");
        }

        if (command.PenaltySeconds <= 0)
        {
            return ConversationErrors.AssignmentPenaltyInvalid(
                "Assignment penalty must be a positive number of seconds.");
        }

        var site = await sites.GetByIdAsync(command.SiteId, cancellationToken);
        if (site is null)
        {
            return ConversationErrors.SiteNotFound(command.SiteId.Value);
        }

        site.UpdateAssignmentPenalty(command.PenaltySeconds, clock.UtcNow);

        var domainEvent = site.DomainEvents.OfType<SiteAssignmentPenaltyUpdated>().Single();
        outbox.Enqueue(SiteAssignmentPenaltyUpdatedMapper.ToEnvelope(domainEvent, idGenerator));
        site.ClearDomainEvents();

        await sites.SaveAsync(site, cancellationToken);

        return site.AssignmentPenaltySeconds;
    }
}
