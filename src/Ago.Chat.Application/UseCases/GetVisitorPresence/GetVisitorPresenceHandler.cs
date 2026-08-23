using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Realtime;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetVisitorPresence;

/// <summary>
/// `5-07`: "which of this operator's own conversations have a currently-connected visitor" - the
/// Scope bullet realtime.md's own connection-registry section says to answer "using whatever the hub
/// already exposes." Investigating for this item found that, as of `5-06`, nothing does: the registry
/// (`IConnectionRegistry`, `Ago.Platform.Abstractions`) has tracked visitor presence since `3-01`, but
/// no hub method or endpoint had ever queried it on an operator's behalf - `OperatorPresencePublisher`
/// only ever *publishes* the operator's own presence loss, one-directional, to the visitor. This
/// handler is the minimal addition that closes that gap: no new infrastructure, just a query over the
/// port that already exists, reusing the exact same `PrincipalKeys.ForVisitor` mapping `3-02`'s
/// fan-out path already established.
///
/// A snapshot, not a push - called when the console opens a conversation, not wired to update live on
/// every connect/disconnect. `realtime.md`'s "advice, not truth" caveat applies here exactly as it
/// does to fan-out delivery: a stale answer means a harmless wrong-looking dot in the UI, not a
/// correctness bug, so a periodic re-query from the client is enough and a new push mechanism is not
/// worth building for this.
/// </summary>
public sealed class GetVisitorPresenceHandler(
    IConversationRepository conversations, IPermissionChecker permissions, IConnectionRegistry registry)
{
    public async Task<Result<bool>> HandleAsync(GetVisitorPresence query, CancellationToken cancellationToken)
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

        var connections = await registry.GetConnectionsAsync(PrincipalKeys.ForVisitor(conversation.VisitorId), cancellationToken);
        return connections.Count > 0;
    }
}
