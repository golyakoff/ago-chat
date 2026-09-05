namespace Ago.Chat.Contracts;

/// <summary>`23-19`: `GET /api/v1/conversations/{conversationId}/channel-deliveries`'s response body.
/// Not keyset-paginated, unlike <see cref="WebhookDeliveryDto"/> - see
/// <c>Ago.Chat.Application.Abstractions.IChannelDeliveryReadStore</c>'s own remarks on why a
/// conversation's own delivery history never grows large enough to need one.</summary>
public sealed record ChannelDeliveryDto(
    Guid Id,
    Guid MessageId,
    string ChannelKind,
    string Status,
    string? ProviderMessageId,
    string? FailureReason,
    DateTimeOffset AttemptedAt);

public sealed record ChannelDeliveriesResponse(IReadOnlyList<ChannelDeliveryDto> Deliveries);
