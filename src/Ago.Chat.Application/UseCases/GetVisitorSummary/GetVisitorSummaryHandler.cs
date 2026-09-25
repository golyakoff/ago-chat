using Ago.Chat.Application.Abstractions;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetVisitorSummary;

/// <summary>
/// `26-114`: the contact-detail panel's own header - "Первый визит {date} · N диалог(ов)"
/// (`docs/design/26-111-thread-contact-detail-panel.md`'s decisions #4/#5, GAP-1/GAP-2). A dedicated,
/// small read rather than growing <see cref="ConversationSummaryDto"/> (that design's own §5-Q3, Option
/// B) - the queue DTO is polled and paginated, and a per-visitor fact only the open thread needs does
/// not belong riding along on every row of it.
///
/// <para><b>Same two access checks as <see cref="GetVisitorHistory.GetVisitorHistoryHandler.HandleAsOperatorAsync"/></b> -
/// RBAC's "may this operator read conversations at all for this site" (`adr/0016`), then the
/// per-conversation "is this operator assigned to *this* one." The header and the returning-visitor list
/// beneath it describe the same visitor and must be reachable by exactly the same caller - a looser or
/// stricter gate here would let the two disagree about who may even see the panel at all.</para>
///
/// <para><b>No access-record write</b> - the identical scoping decision
/// <see cref="GetVisitorHistory.GetVisitorHistoryHandler.HandleAsOperatorAsync"/> already makes for its
/// own list (that handler's own remarks): a name, a date and a count are not the boundary-crossing read
/// `24-12`'s own Scope means by "access" - only actually opening a different conversation's real message
/// history is.</para>
/// </summary>
public sealed class GetVisitorSummaryHandler(
    IConversationRepository conversations,
    IConversationReadStore readStore,
    IPermissionChecker permissions)
{
    public async Task<Result<VisitorSummaryResponse>> HandleAsOperatorAsync(
        GetVisitorSummary query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.ConversationRead, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to read conversations for this site.");
        }

        var conversation = await conversations.GetByIdAsync(query.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return ConversationErrors.NotFound(query.ConversationId.Value);
        }

        if (conversation.OperatorId != query.RequestedBy)
        {
            return ConversationErrors.Forbidden("This operator is not assigned to this conversation.");
        }

        // `24-10`: the same "the panel's own anchor conversation is blocked -> unreachable" rule
        // GetVisitorHistoryHandler applies to itself - this header sits above that same panel.
        if (conversation.IsBlocked)
        {
            return ConversationErrors.NotFound(query.ConversationId.Value);
        }

        var summary = await readStore.GetVisitorSummaryAsync(conversation.VisitorId, cancellationToken);
        return new VisitorSummaryResponse(summary.FirstSeenAt, summary.ConversationCount);
    }
}
