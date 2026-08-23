using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// The write-side port for <see cref="WebhookDelivery"/> rows - this item's own scope has no real
/// caller for it yet (`6-05`'s dispatcher is), but its own tests need to write rows directly to prove
/// the delivery-history read side without a real HTTP send (this item's own scope note).
///
/// `6-05`: <see cref="SaveAsync"/> now returns whether the row was actually the first one recorded
/// for its <c>(EndpointId, MessageId)</c> pair - the same "true = first delivery, false = a
/// duplicate was skipped" shape <c>Ago.Platform.Abstractions.IInboxChecker.TryRecordAndSaveAsync</c>
/// already uses, and for the same reason: whether a redelivered event already reached this endpoint
/// is only knowable from the outcome of the actual write (the unique-index violation), not from a
/// prior read alone. <see cref="ExistsAsync"/> is the cheap early-skip a dispatcher calls *before*
/// spending an HTTP round trip on an endpoint it already knows was handled - a genuine optimisation,
/// not the correctness guarantee; the unique index (and this method's own handling of violating it)
/// is what still holds if two overlapping attempts both pass that check.
/// </summary>
public interface IWebhookDeliveryRepository
{
    /// <returns><see langword="true"/> if this delivery was newly recorded; <see langword="false"/>
    /// if a row already existed for the same <c>(EndpointId, MessageId)</c> pair and nothing was
    /// written - the caller's own HTTP attempt already happened by this point either way (this method
    /// only decides whether it gets counted), the same at-least-once tradeoff
    /// `docs/architecture/messaging.md` accepts everywhere else: the receiver's own signature-carried
    /// idempotency is the backstop for a physical duplicate send, not this ledger alone.</returns>
    Task<bool> SaveAsync(WebhookDelivery delivery, CancellationToken cancellationToken);

    /// <summary>The fast-path idempotency pre-check described above - never the sole guarantee.</summary>
    Task<bool> ExistsAsync(WebhookEndpointId endpointId, Guid messageId, CancellationToken cancellationToken);
}
