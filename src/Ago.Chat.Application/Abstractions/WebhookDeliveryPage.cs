using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// A keyset page over one endpoint's delivery history, newest-first (data-model.md: `OFFSET` is
/// banned) - the same shape <see cref="ConversationListPage"/> already uses for the analogous
/// "unbounded, only-grows, admin-facing list" case. Cursor is a delivery id, not a timestamp -
/// delivery ids are uuid v7 (`IIdGenerator`), so id order already is creation order, the same reason
/// <see cref="ConversationListPage"/>'s own remarks give for using `id` over a second cursor column.
/// </summary>
public sealed record WebhookDeliveryPage(IReadOnlyList<WebhookDeliverySummaryItem> Deliveries, Guid? NextBeforeId);

public sealed record WebhookDeliverySummaryItem(
    WebhookDeliveryId Id,
    string EventType,
    int Attempt,
    WebhookDeliveryStatus Status,
    int? ResponseStatus,
    string? ResponseSnippet,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeliveredAt);
