using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.ListModuleTaskChannelPriorityList;

/// <summary>
/// `20-11`: the read side of the priority list - two access checks matching
/// <c>ListChannelIdentitiesForVisitorHandler</c> exactly (`adr/0016`'s split). Unlike the write side, a
/// conversation with no <see cref="Domain.Conversation.ActiveModuleTask"/> is not an error here - it
/// simply has nothing to list yet, the same "empty is a valid answer, not a failure" shape an empty
/// <see cref="Domain.ChannelIdentity"/> list already has for a brand-new visitor.
/// </summary>
public sealed class ListModuleTaskChannelPriorityListHandler(
    IConversationRepository conversations,
    IModuleTaskChannelPreferenceRepository preferences,
    IChannelIdentityRepository identities,
    IPermissionChecker permissions)
{
    public async Task<Result<IReadOnlyList<ModuleTaskChannelPreferenceSummary>>> HandleAsync(
        ListModuleTaskChannelPriorityList query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.ConversationRead, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to read conversations for this site.");
        }

        var conversation = await conversations.GetByIdAsync(query.ConversationId, cancellationToken);
        if (conversation is null || conversation.SiteId != query.SiteId)
        {
            return ConversationErrors.NotFound(query.ConversationId.Value);
        }

        if (conversation.OperatorId != query.RequestedBy)
        {
            return ConversationErrors.Forbidden("This operator is not assigned to this conversation.");
        }

        var activeTask = conversation.ActiveModuleTask;
        if (activeTask is null)
        {
            return Result<IReadOnlyList<ModuleTaskChannelPreferenceSummary>>.Success([]);
        }

        var rows = await preferences.ListForModuleTaskAsync(activeTask.Id, cancellationToken);

        var summaries = new List<ModuleTaskChannelPreferenceSummary>(rows.Count);
        foreach (var row in rows)
        {
            var identity = await identities.GetByIdAsync(row.ChannelIdentityId, cancellationToken);
            if (identity is null)
            {
                // Should not happen (a ChannelIdentity is never deleted, only unlinked) - defensive
                // skip rather than a null-reference failure, the same posture
                // ListChannelIdentitiesForVisitorHandler's own remarks describe for its analogous
                // "should not happen" visitor lookup.
                continue;
            }

            summaries.Add(new ModuleTaskChannelPreferenceSummary(
                identity.Id.Value, identity.Kind, identity.Address.Value, row.Priority, row.AddedAt, identity.Active));
        }

        return Result<IReadOnlyList<ModuleTaskChannelPreferenceSummary>>.Success(summaries);
    }
}
