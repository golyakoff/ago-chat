using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.ListChannelIdentitiesForVisitor;

/// <summary>
/// `14-12`: the read behind the console's own <c>VisitorPanel</c> channel-identity list - two access
/// checks, matching <c>GetVisitorHistoryHandler</c>'s own operator entry point exactly (`adr/0016`'s
/// split: RBAC answers "may this operator read conversations at all for this site", the per-conversation
/// comparison answers "may this operator read *this* one"). Reusing that exact shape here, rather than
/// widening it, keeps this item's own scope to "a panel on the conversation an operator is already
/// looking at" - a site-wide visitor/identity lookup is a different, unbuilt feature
/// (`GetVisitorHistoryHandler`'s own remarks name the identical boundary for its own panel).
/// </summary>
public sealed class ListChannelIdentitiesForVisitorHandler(
    IConversationRepository conversations, IChannelIdentityRepository identities, IPermissionChecker permissions)
{
    public async Task<Result<IReadOnlyList<ChannelIdentitySummary>>> HandleAsync(
        ListChannelIdentitiesForVisitor query, CancellationToken cancellationToken)
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

        var active = await identities.ListActiveForVisitorAsync(conversation.VisitorId, cancellationToken);
        IReadOnlyList<ChannelIdentitySummary> summaries = active
            .Select(i => new ChannelIdentitySummary(i.Id.Value, i.Kind, i.Address.Value, i.FirstSeenAt, i.LastSeenAt))
            .ToList();
        return Result<IReadOnlyList<ChannelIdentitySummary>>.Success(summaries);
    }
}
