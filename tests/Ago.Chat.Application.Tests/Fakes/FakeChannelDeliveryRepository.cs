using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>`23-19`: mimics the real repository's own unique-<c>message_id</c> semantics in memory -
/// <see cref="SaveAsync"/> returns <see langword="false"/> for a duplicate <see cref="ChannelDelivery.MessageId"/>
/// instead of throwing, the same "already recorded, no-op" contract
/// <c>Ago.Chat.Infrastructure.Postgres.ChannelDeliveryRepository</c> gives via a caught unique-index
/// violation - <see cref="FakeWebhookDeliveryRepository"/>'s own shape, at this table's own key.</summary>
public sealed class FakeChannelDeliveryRepository : IChannelDeliveryRepository
{
    public List<ChannelDelivery> Saved { get; } = [];

    public Task<bool> SaveAsync(ChannelDelivery delivery, CancellationToken cancellationToken)
    {
        if (Saved.Any(d => d.MessageId == delivery.MessageId))
        {
            return Task.FromResult(false);
        }

        Saved.Add(delivery);
        return Task.FromResult(true);
    }
}
