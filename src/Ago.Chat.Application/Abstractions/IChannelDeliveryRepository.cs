using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `23-19`: the write-side port for <see cref="ChannelDelivery"/> - <c>WebhookDeliveryRepository</c>'s
/// own "insert-only, catch the unique violation" shape (<see cref="IWebhookDeliveryRepository"/>'s own remarks),
/// reused for the identical reason: whether a redelivered <c>MessageAccepted</c> already wrote this row
/// is only knowable from the outcome of the actual insert, not from a prior read.
/// </summary>
public interface IChannelDeliveryRepository
{
    /// <returns><see langword="true"/> if this delivery was newly recorded; <see langword="false"/> if a
    /// row already existed for the same <see cref="ChannelDelivery.MessageId"/> and nothing was written -
    /// the adapter's own send already happened by this point either way (this method only decides
    /// whether the outcome gets recorded a second time), the same at-least-once tradeoff
    /// `messaging.md` accepts everywhere else.</returns>
    Task<bool> SaveAsync(ChannelDelivery delivery, CancellationToken cancellationToken);
}
