using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeChannelDeliveryReadStore : IChannelDeliveryReadStore
{
    private readonly List<(ConversationId ConversationId, SiteId SiteId, ChannelDeliverySummaryItem Item)> _items = [];

    public void Seed(ConversationId conversationId, SiteId siteId, ChannelDeliverySummaryItem item) =>
        _items.Add((conversationId, siteId, item));

    public Task<IReadOnlyList<ChannelDeliverySummaryItem>> GetForConversationAsync(
        ConversationId conversationId, SiteId siteId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ChannelDeliverySummaryItem>>(_items
            .Where(x => x.ConversationId == conversationId && x.SiteId == siteId)
            .Select(x => x.Item)
            .ToList());
}
