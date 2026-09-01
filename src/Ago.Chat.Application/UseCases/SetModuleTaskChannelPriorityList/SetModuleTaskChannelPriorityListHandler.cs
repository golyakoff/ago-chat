using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.SetModuleTaskChannelPriorityList;

/// <summary>
/// `20-11`: the deferred second half of `20-09`'s own primary-phone gate - a visitor's priority-ordered
/// list of additional verified contact channels for the conversation's *current active booking*
/// (`Conversation.ActiveModuleTask`), not the visitor as a whole (`14-13`'s own
/// <see cref="Visitor.PreferredChannelIdentityId"/> already covers that, unchanged).
///
/// <para><b>Gated on <see cref="Permission.ConversationSend"/>, plus the identical "assigned operator
/// only" per-conversation check, matching <c>SetPreferredChannelIdentityHandler</c> exactly</b> - the
/// same reasoning that handler's own remarks give applies unchanged here: this action only ever
/// redirects where a booking-related reply the operator is already trusted to send goes, and only among
/// identities that were already verified by `14-12`/`14-15`'s own evidence-based mechanisms. It grants no
/// new access to a channel credential.</para>
///
/// <para><b>Every entry must independently survive the identical eligibility check `14-13`'s own
/// preference uses</b> - exists, belongs to this site, belongs to *this conversation's own visitor*, and
/// is still <see cref="ChannelIdentity.Active"/>. This is the entire enforcement of "never a place in the
/// list until independently verified": there is no other way a caller can name a
/// <see cref="ChannelIdentityId"/> that would pass this check without having gone through `14-12`'s
/// inbound-message evidence or `14-15`'s confirmed-code evidence first.</para>
///
/// <para><b>Whole-list replace, not incremental add/remove/reorder</b> - "the priority order a visitor
/// sets is stored and retrievable" is satisfied by treating the submitted order as the entire, current
/// truth, the same way `PUT .../preference` treats its single value. This also sidesteps ever needing a
/// concurrent "insert at position 2" operation to reason about.</para>
/// </summary>
public sealed class SetModuleTaskChannelPriorityListHandler(
    IConversationRepository conversations,
    IChannelIdentityRepository identities,
    IModuleTaskChannelPreferenceRepository preferences,
    IPermissionChecker permissions,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result> HandleAsync(SetModuleTaskChannelPriorityList command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.ConversationSend, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to send messages in this conversation.");
        }

        var conversation = await conversations.GetByIdAsync(command.ConversationId, cancellationToken);
        if (conversation is null || conversation.SiteId != command.SiteId)
        {
            return ConversationErrors.NotFound(command.ConversationId.Value);
        }

        if (conversation.OperatorId != command.RequestedBy)
        {
            return ConversationErrors.Forbidden("This operator is not assigned to this conversation.");
        }

        var activeTask = conversation.ActiveModuleTask;
        if (activeTask is null)
        {
            return ConversationErrors.ModuleTaskChannelPriorityNoActiveTask();
        }

        var ids = command.ChannelIdentityIdsInPriorityOrder;
        var seen = new HashSet<ChannelIdentityId>();
        foreach (var id in ids)
        {
            if (!seen.Add(id))
            {
                return ConversationErrors.ModuleTaskChannelPriorityDuplicateEntry(id.Value);
            }
        }

        foreach (var id in ids)
        {
            var identity = await identities.GetByIdAsync(id, cancellationToken);
            if (identity is null
                || identity.SiteId != command.SiteId
                || identity.VisitorId != conversation.VisitorId
                || !identity.Active)
            {
                return ConversationErrors.ModuleTaskChannelNotEligible(id.Value);
            }
        }

        var now = clock.UtcNow;
        var rows = ids
            .Select((id, index) => ModuleTaskChannelPreference.Add(
                new ModuleTaskChannelPreferenceId(idGenerator.NewId(now)), command.SiteId, activeTask.Id,
                conversation.VisitorId, id, priority: index + 1, now))
            .ToList();

        await preferences.ReplaceForModuleTaskAsync(activeTask.Id, rows, cancellationToken);

        return Result.Success();
    }
}
