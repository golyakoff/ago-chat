using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// The write-side port for <see cref="WebhookDelivery"/> rows - this item's own scope has no real
/// caller for it yet (`6-05`'s dispatcher is), but its own tests need to write rows directly to prove
/// the delivery-history read side without a real HTTP send (this item's own scope note). Save-only,
/// no update: `6-05` will decide its own shape for recording a retried attempt (a new row per attempt,
/// or an update-in-place) when it has a real dispatch loop to design that against - guessing at it now
/// would be exactly the "port shaped by a caller that does not exist yet" mistake
/// `clean-architecture.md` warns against.
/// </summary>
public interface IWebhookDeliveryRepository
{
    Task SaveAsync(WebhookDelivery delivery, CancellationToken cancellationToken);
}
