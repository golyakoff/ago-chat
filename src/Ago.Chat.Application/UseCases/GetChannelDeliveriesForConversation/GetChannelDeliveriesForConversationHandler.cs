using Ago.Chat.Application.Abstractions;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetChannelDeliveriesForConversation;

/// <summary>
/// `23-19`: "gated the same way conversation reads are" - the item's own Scope. That means exactly
/// <see cref="Application.UseCases.GetConversationHistory.GetConversationHistoryHandler.HandleAsOperatorAsync"/>'s
/// own two checks, reused rather than invented fresh: <see cref="Permission.ConversationRead"/>, and the
/// caller must be *this conversation's own assigned operator* - not merely someone with read access
/// somewhere on the site. A delivery record answers "did my customer's message reach them", which is
/// exactly as sensitive as the conversation it is about; there is no argument for widening it to every
/// operator with <c>conversation:read</c> when the conversation read itself does not extend that
/// far.
/// </summary>
public sealed class GetChannelDeliveriesForConversationHandler(
    IConversationRepository conversations, IChannelDeliveryReadStore deliveries, IPermissionChecker permissions)
{
    public async Task<Result<ChannelDeliveriesResponse>> HandleAsync(
        GetChannelDeliveriesForConversation query, CancellationToken cancellationToken)
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
            // Same as GetConversationHistoryHandler.HandleAsOperatorAsync's own check: no separate
            // conversation.SiteId comparison is needed alongside it. RequestedBy already passed the
            // permission check above scoped to query.SiteId, so OperatorId == RequestedBy already
            // implies the conversation is this operator's own site's - an operator is never assigned to
            // a conversation outside their own site.
            return ConversationErrors.Forbidden("This operator is not assigned to this conversation.");
        }

        var rows = await deliveries.GetForConversationAsync(query.ConversationId, query.SiteId, cancellationToken);
        return new ChannelDeliveriesResponse(rows.Select(ToDto).ToList());
    }

    private static ChannelDeliveryDto ToDto(ChannelDeliverySummaryItem item) => new(
        item.Id.Value, item.MessageId.Value, item.ChannelKind.ToString(), item.Status.ToString(),
        item.ProviderMessageId, item.FailureReason, item.AttemptedAt);
}
