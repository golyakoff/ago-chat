using Ago.Chat.Application.Abstractions;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetOperatorQueue;

/// <summary>
/// `5-07`: the console's queue/dashboard view needs a way to learn "what's waiting for my site, what's
/// assigned to me" on load and after a page refresh - a real gap found while building the console, not
/// anticipated by any earlier item: `4-02`'s automatic assignment engine notifies a *connected*
/// operator of a new assignment over the hub (`"ConversationAssigned"`, `ResolveConversationAssignmentTargetsHandler`),
/// but nothing answers "what do I already have" for an operator who just opened the console. A pure
/// query, no Domain step - it neither raises nor enforces a business invariant, only reads two lists
/// `IConversationRepository` already knows how to produce (`GetWaitingForSiteAsync`,
/// `GetAssignedToOperatorAsync` - `4-04`'s existing method, reused here rather than duplicated).
///
/// <para><b>`18-04`'s tag filter, applied in-memory rather than pushed into the repository query.</b>
/// <see cref="ITagRepository"/> shares no query surface with <see cref="IConversationRepository"/> - it
/// answers "which conversation ids carry this tag" as its own small, bounded set
/// (<see cref="ITagRepository.GetConversationIdsForTagAsync"/>), which this handler intersects against
/// the two lists it already loaded. That is the same shape <see cref="IConversationRepository"/>'s own
/// remarks already justify for those two reads (small, bounded, unpaginated) - adding a join to the EF
/// query would touch a write-side port for a read-only filter, and threading a new parameter through
/// <see cref="IConversationRepository.GetAssignedToOperatorAsync"/> would also change
/// <c>OperatorConversationReleaser</c>'s unrelated call to it for no reason. `GetAllConversationsForSiteHandler`
/// makes the opposite call for its own genuinely paginated read - see that handler's own remarks.</para>
/// </summary>
public sealed class GetOperatorQueueHandler(
    IConversationRepository conversations, ITagRepository tags, IPermissionChecker permissions)
{
    public async Task<Result<OperatorQueueResponse>> HandleAsync(GetOperatorQueue query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.OperatorId, query.SiteId, Permission.ConversationRead, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to read conversations for this site.");
        }

        var waiting = await conversations.GetWaitingForSiteAsync(query.SiteId, cancellationToken);
        var assigned = await conversations.GetAssignedToOperatorAsync(query.OperatorId, cancellationToken);

        if (query.Tag is { } tagId)
        {
            var taggedIds = await tags.GetConversationIdsForTagAsync(tagId, query.SiteId, cancellationToken);
            waiting = waiting.Where(c => taggedIds.Contains(c.Id)).ToList();
            assigned = assigned.Where(c => taggedIds.Contains(c.Id)).ToList();
        }

        return new OperatorQueueResponse(waiting.Select(ToSummary).ToList(), assigned.Select(ToSummary).ToList());
    }

    private static ConversationSummaryDto ToSummary(Conversation conversation) => new(
        conversation.Id.Value, conversation.VisitorId.Value, conversation.State.ToString(),
        conversation.CreatedAt, conversation.OperatorUnreadCount, conversation.OperatorId?.Value);
}
