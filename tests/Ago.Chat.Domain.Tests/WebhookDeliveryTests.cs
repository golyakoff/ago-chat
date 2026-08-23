namespace Ago.Chat.Domain.Tests;

public class WebhookDeliveryTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly WebhookEndpointId EndpointId = new(Guid.NewGuid());

    [Fact]
    public void Record_WithAResponseSnippetUnderTheLimit_KeepsItInFull()
    {
        var delivery = WebhookDelivery.Record(
            new WebhookDeliveryId(Guid.NewGuid()), EndpointId, Guid.NewGuid(), "message.created", "{}", attempt: 1,
            WebhookDeliveryStatus.Delivered, responseStatus: 200, responseSnippet: "OK", Now, Now);

        Assert.Equal("OK", delivery.ResponseSnippet);
    }

    [Fact]
    public void Record_WithAResponseSnippetOverTheLimit_TruncatesIt()
    {
        var oversized = new string('x', WebhookDelivery.MaxResponseSnippetLength + 500);

        var delivery = WebhookDelivery.Record(
            new WebhookDeliveryId(Guid.NewGuid()), EndpointId, Guid.NewGuid(), "message.created", "{}", attempt: 1,
            WebhookDeliveryStatus.Failed, responseStatus: 500, responseSnippet: oversized, Now, deliveredAt: null);

        Assert.Equal(WebhookDelivery.MaxResponseSnippetLength, delivery.ResponseSnippet!.Length);
    }

    [Fact]
    public void Record_CanConstructAPendingDeliveryWithNoResponseYet()
    {
        var delivery = WebhookDelivery.Record(
            new WebhookDeliveryId(Guid.NewGuid()), EndpointId, Guid.NewGuid(), "message.created", "{}", attempt: 1,
            WebhookDeliveryStatus.Pending, responseStatus: null, responseSnippet: null, Now, deliveredAt: null);

        Assert.Equal(WebhookDeliveryStatus.Pending, delivery.Status);
        Assert.Null(delivery.ResponseStatus);
        Assert.Null(delivery.DeliveredAt);
    }

    [Fact]
    public void Record_KeepsTheMessageIdItWasGiven()
    {
        // `6-05`: this is the field the (endpoint_id, message_id) unique index dedups on
        // (WebhookDeliveryConfiguration) - a plain round-trip check that the aggregate does not
        // silently drop or substitute it.
        var messageId = Guid.NewGuid();

        var delivery = WebhookDelivery.Record(
            new WebhookDeliveryId(Guid.NewGuid()), EndpointId, messageId, "message.created", "{}", attempt: 1,
            WebhookDeliveryStatus.Delivered, responseStatus: 200, responseSnippet: "OK", Now, Now);

        Assert.Equal(messageId, delivery.MessageId);
    }
}
