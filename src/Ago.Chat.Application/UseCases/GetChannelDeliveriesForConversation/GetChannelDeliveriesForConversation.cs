using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetChannelDeliveriesForConversation;

/// <summary>`23-19`: the per-conversation read the item's own Scope asks for - "gated the same way
/// conversation reads are." <see cref="GetChannelDeliveriesForConversationHandler"/>'s own remarks for
/// exactly which existing check that means.</summary>
public sealed record GetChannelDeliveriesForConversation(ConversationId ConversationId, SiteId SiteId, OperatorId RequestedBy);
