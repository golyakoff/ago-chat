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
/// </summary>
public sealed class GetOperatorQueueHandler(IConversationRepository conversations, IPermissionChecker permissions)
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

        return new OperatorQueueResponse(waiting.Select(ToSummary).ToList(), assigned.Select(ToSummary).ToList());
    }

    private static ConversationSummaryDto ToSummary(Conversation conversation) => new(
        conversation.Id.Value, conversation.VisitorId.Value, conversation.State.ToString(),
        conversation.CreatedAt, conversation.OperatorUnreadCount);
}
