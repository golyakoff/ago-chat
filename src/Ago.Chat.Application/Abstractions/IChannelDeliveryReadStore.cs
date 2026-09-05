using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `23-19`: the read-side port for delivery history: hand-written SQL over the write model, never
/// through the aggregate (adr/0004) - the same split <see cref="IWebhookDeliveryReadStore"/> already
/// draws for its own table. Unbounded per conversation, unlike the webhook history's keyset page -
/// a channel conversation carries at most a few dozen operator messages before this item's own
/// <c>ChannelDeliveryPruneJob</c> window expires the oldest ones, nowhere near the volume
/// `WebhookDeliveryPage`'s cursor exists to bound.
/// </summary>
public interface IChannelDeliveryReadStore
{
    /// <summary>Every delivery recorded for this conversation, newest attempt first. Gated by the
    /// caller (<c>GetChannelDeliveriesForConversationHandler</c>) the same way conversation reads
    /// already are - this store itself only enforces the tenant boundary via <paramref name="siteId"/>,
    /// never trusts <paramref name="conversationId"/> alone.</summary>
    Task<IReadOnlyList<ChannelDeliverySummaryItem>> GetForConversationAsync(
        ConversationId conversationId, SiteId siteId, CancellationToken cancellationToken);
}

public sealed record ChannelDeliverySummaryItem(
    ChannelDeliveryId Id,
    MessageId MessageId,
    ChannelKind ChannelKind,
    ChannelDeliveryStatus Status,
    string? ProviderMessageId,
    string? FailureReason,
    DateTimeOffset AttemptedAt);
