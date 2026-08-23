namespace Ago.Chat.Domain;

/// <summary>
/// `6-03`: the delivery-attempt lifecycle a future dispatcher (`6-05`, `adr/0013`) writes - this item
/// only defines the vocabulary and writes rows directly (repository-level, no dispatcher exists yet),
/// so no transition method enforces movement between these today; `6-05` is the first real caller of
/// a state *transition*, the same "no domain-event plumbing ahead of a real subscriber" discipline
/// `Attachment`'s own remarks already apply to this codebase.
/// </summary>
public enum WebhookDeliveryStatus
{
    Pending,
    Delivered,
    Failed,
    DeadLettered,
}
