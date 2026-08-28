using System.Text.Json;

namespace Ago.Chat.Infrastructure.YooKassa;

/// <summary>
/// `13-02`: the one place ЮKassa's webhook JSON becomes something worth acting on - a pure function,
/// the same shape <c>MaxInboundMessageParser.TryParse</c>/<c>TelegramInboundMessageParser.TryParse</c>
/// already establish, called directly by <c>Ago.Chat.Api</c>'s own billing webhook endpoint (the
/// identical "endpoint deserializes the provider envelope and calls this parser" split
/// <c>MaxWebhookEndpoints</c> uses for MAX's own inbound webhook).
/// </summary>
public static class YooKassaWebhookParser
{
    public static ParsedYooKassaWebhookEvent? TryParse(string rawBody)
    {
        YooKassaWebhookEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<YooKassaWebhookEnvelope>(rawBody);
        }
        catch (JsonException)
        {
            return null;
        }

        if (envelope is null || string.IsNullOrEmpty(envelope.Event) || string.IsNullOrEmpty(envelope.Object?.Id))
        {
            return null;
        }

        return new ParsedYooKassaWebhookEvent(envelope.Event, envelope.Object.Id, envelope.Object.PaymentMethod?.Id);
    }
}

public sealed record ParsedYooKassaWebhookEvent(string EventType, string YooKassaPaymentId, string? PaymentMethodId);
