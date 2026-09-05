namespace Ago.Chat.Domain.Tests;

public class ChannelDeliveryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly ConversationId ConversationId = new(Guid.NewGuid());
    private static readonly ChannelIdentityId ChannelIdentityId = new(Guid.NewGuid());

    [Fact]
    public void Record_ForADeliveredOutcome_KeepsTheProviderMessageIdAndNoFailureReason()
    {
        var messageId = new MessageId(Guid.NewGuid());

        var delivery = ChannelDelivery.Record(
            new ChannelDeliveryId(Guid.NewGuid()), SiteId, ConversationId, messageId, ChannelKind.Sms,
            ChannelIdentityId, ChannelDeliveryStatus.Delivered, providerMessageId: "sms-provider-123",
            failureReason: null, Now);

        Assert.Equal(messageId, delivery.MessageId);
        Assert.Equal(ChannelDeliveryStatus.Delivered, delivery.Status);
        Assert.Equal("sms-provider-123", delivery.ProviderMessageId);
        Assert.Null(delivery.FailureReason);
    }

    [Fact]
    public void Record_ForARefusedOutcome_KeepsTheFailureReasonAndNoProviderMessageId()
    {
        var delivery = ChannelDelivery.Record(
            new ChannelDeliveryId(Guid.NewGuid()), SiteId, ConversationId, new MessageId(Guid.NewGuid()), ChannelKind.Sms,
            ChannelIdentityId, ChannelDeliveryStatus.Refused, providerMessageId: null, failureReason: "unknown number", Now);

        Assert.Equal(ChannelDeliveryStatus.Refused, delivery.Status);
        Assert.Equal("unknown number", delivery.FailureReason);
        Assert.Null(delivery.ProviderMessageId);
    }

    [Fact]
    public void Record_WithAFailureReasonOverTheLimit_TruncatesIt()
    {
        var oversized = new string('x', ChannelDelivery.MaxProviderDetailLength + 500);

        var delivery = ChannelDelivery.Record(
            new ChannelDeliveryId(Guid.NewGuid()), SiteId, ConversationId, new MessageId(Guid.NewGuid()), ChannelKind.Sms,
            ChannelIdentityId, ChannelDeliveryStatus.Refused, providerMessageId: null, failureReason: oversized, Now);

        Assert.Equal(ChannelDelivery.MaxProviderDetailLength, delivery.FailureReason!.Length);
    }

    [Fact]
    public void Record_KeepsTheChannelIdentityReference_NotAnAddress()
    {
        // `23-19`: the address-versus-reference decision - this type carries ChannelIdentityId, never
        // an ExternalChannelAddress. This test is a compile-time-shaped guard as much as a runtime one:
        // there is no address parameter to accidentally wire up.
        var delivery = ChannelDelivery.Record(
            new ChannelDeliveryId(Guid.NewGuid()), SiteId, ConversationId, new MessageId(Guid.NewGuid()), ChannelKind.Telegram,
            ChannelIdentityId, ChannelDeliveryStatus.Delivered, providerMessageId: "tg-1", failureReason: null, Now);

        Assert.Equal(ChannelIdentityId, delivery.ChannelIdentityId);
    }
}
