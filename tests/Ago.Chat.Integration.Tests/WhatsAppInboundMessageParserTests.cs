using Ago.Chat.Infrastructure.WhatsApp;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `14-10`: <see cref="WhatsAppInboundMessageParser"/> is a pure function with no infrastructure
/// dependency of its own - <see cref="VkInboundMessageParserTests"/>'s own precedent for living in
/// <c>Ago.Chat.Integration.Tests</c> rather than a dedicated <c>Ago.Chat.Infrastructure.WhatsApp.Tests</c>
/// project, for the identical pragmatic reason that class states for itself.
///
/// <para><b>Honesty note.</b> Unlike <c>MaxInboundMessageParserTests</c>'/<c>VkInboundMessageParserTests</c>'
/// own versions of this note, developers.facebook.com was directly reachable from this environment, so
/// every field name asserted below is confirmed from Meta's own current documentation
/// (<c>WhatsAppDtos.cs</c>'s own citation) rather than reconstructed from a third party. What this class
/// proves is that the parser's own logic (walk every entry/change/message, recognise only text messages,
/// skip a status-only delivery, extract phone_number_id/from/text/id) behaves correctly against the shape
/// this item's own research found - not that a real WhatsApp Business number's own delivery matches it
/// byte for byte, which this item's own report names as unverified.</para>
/// </summary>
public sealed class WhatsAppInboundMessageParserTests
{
    private static WhatsAppWebhookEnvelope SingleTextMessage(
        string phoneNumberId = "106540352242922", string from = "16505551234", string text = "hello there",
        string? id = "wamid.HBgLMTY1MDU1NTEyMzQVAgARGBI5QTNDQTVCM0Q0Q0Q2RTY3RTcA", string type = "text") =>
        new(
            "whatsapp_business_account",
            [
                new WhatsAppEntry(
                    "entry-1",
                    [
                        new WhatsAppChange(
                            "messages",
                            new WhatsAppChangeValue(
                                "whatsapp",
                                new WhatsAppMetadata("15555555555", phoneNumberId),
                                [new WhatsAppMessage(from, id, "1700000000", type, new WhatsAppMessageText(text))],
                                Statuses: null)),
                    ]),
            ]);

    [Fact]
    public void Parse_ForATextMessage_ExtractsPhoneNumberIdFromTextAndId()
    {
        var envelope = SingleTextMessage();

        var parsed = WhatsAppInboundMessageParser.Parse(envelope);

        var message = Assert.Single(parsed);
        Assert.Equal("106540352242922", message.PhoneNumberId);
        Assert.Equal("16505551234", message.From);
        Assert.Equal("hello there", message.Text);
        Assert.Equal("wamid.HBgLMTY1MDU1NTEyMzQVAgARGBI5QTNDQTVCM0Q0Q0Q2RTY3RTcA", message.ExternalMessageId);
    }

    /// <summary>WhatsAppChangeValue's own remarks: a status-only delivery (an operator's own outbound
    /// reply being marked delivered/read) carries <c>statuses</c> instead of <c>messages</c> under the
    /// identical <c>changes[].field == "messages"</c> discriminator - this parser's own version of the
    /// hazard <c>VkInboundMessageParser</c>'s <c>out == 1</c> filter solves for VK.</summary>
    [Fact]
    public void Parse_ForAStatusOnlyDelivery_ReturnsNoMessages()
    {
        var envelope = new WhatsAppWebhookEnvelope(
            "whatsapp_business_account",
            [
                new WhatsAppEntry(
                    "entry-1",
                    [
                        new WhatsAppChange(
                            "messages",
                            new WhatsAppChangeValue(
                                "whatsapp",
                                new WhatsAppMetadata("15555555555", "106540352242922"),
                                Messages: null,
                                Statuses: System.Text.Json.JsonSerializer.SerializeToElement(new[] { new { id = "wamid.1", status = "delivered" } }))),
                    ]),
            ]);

        Assert.Empty(WhatsAppInboundMessageParser.Parse(envelope));
    }

    /// <summary>WhatsAppMessage's own remarks: only <c>"text"</c> is recognised - a non-text message
    /// (image, audio, location, an interactive reply) is skipped rather than coerced into a text-shaped
    /// stand-in, `14-06`'s own scope, not this item's.</summary>
    [Theory]
    [InlineData("image")]
    [InlineData("audio")]
    [InlineData("location")]
    [InlineData("interactive")]
    public void Parse_ForANonTextMessageType_ReturnsNoMessages(string type)
    {
        var envelope = SingleTextMessage(type: type);

        Assert.Empty(WhatsAppInboundMessageParser.Parse(envelope));
    }

    [Fact]
    public void Parse_WithNoFrom_ReturnsNoMessages()
    {
        var envelope = new WhatsAppWebhookEnvelope(
            "whatsapp_business_account",
            [
                new WhatsAppEntry(
                    "entry-1",
                    [
                        new WhatsAppChange(
                            "messages",
                            new WhatsAppChangeValue(
                                "whatsapp",
                                new WhatsAppMetadata("15555555555", "106540352242922"),
                                [new WhatsAppMessage(null, "wamid.1", "1700000000", "text", new WhatsAppMessageText("hi"))],
                                Statuses: null)),
                    ]),
            ]);

        Assert.Empty(WhatsAppInboundMessageParser.Parse(envelope));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_WithNoText_ReturnsNoMessages(string? text)
    {
        var envelope = SingleTextMessage(text: text ?? "");

        Assert.Empty(WhatsAppInboundMessageParser.Parse(envelope));
    }

    [Fact]
    public void Parse_WithNoId_ReturnsNoMessages()
    {
        var envelope = SingleTextMessage(id: null);

        Assert.Empty(WhatsAppInboundMessageParser.Parse(envelope));
    }

    [Fact]
    public void Parse_WithNoPhoneNumberIdInMetadata_ReturnsNoMessages()
    {
        var envelope = SingleTextMessage(phoneNumberId: "");

        Assert.Empty(WhatsAppInboundMessageParser.Parse(envelope));
    }

    /// <summary><see cref="WhatsAppEntry"/>'s own remarks - Meta's own envelope is natively a batch
    /// container, and this parser must walk every entry/change/message rather than only the first, or a
    /// real batched delivery would silently lose every message after the first.</summary>
    [Fact]
    public void Parse_WithMultipleEntriesAndMessages_ExtractsAllOfThem()
    {
        var envelope = new WhatsAppWebhookEnvelope(
            "whatsapp_business_account",
            [
                new WhatsAppEntry(
                    "entry-1",
                    [
                        new WhatsAppChange(
                            "messages",
                            new WhatsAppChangeValue(
                                "whatsapp",
                                new WhatsAppMetadata("15555555555", "106540352242922"),
                                [
                                    new WhatsAppMessage("16505551234", "wamid.1", "1700000000", "text", new WhatsAppMessageText("first")),
                                    new WhatsAppMessage("16505551234", "wamid.2", "1700000001", "text", new WhatsAppMessageText("second")),
                                ],
                                Statuses: null)),
                    ]),
                new WhatsAppEntry(
                    "entry-2",
                    [
                        new WhatsAppChange(
                            "messages",
                            new WhatsAppChangeValue(
                                "whatsapp",
                                new WhatsAppMetadata("15555555556", "999999999"),
                                [new WhatsAppMessage("16505559999", "wamid.3", "1700000002", "text", new WhatsAppMessageText("third"))],
                                Statuses: null)),
                    ]),
            ]);

        var parsed = WhatsAppInboundMessageParser.Parse(envelope);

        Assert.Equal(3, parsed.Count);
        Assert.Equal(["first", "second", "third"], parsed.Select(m => m.Text));
        Assert.Equal(["106540352242922", "106540352242922", "999999999"], parsed.Select(m => m.PhoneNumberId));
    }

    [Fact]
    public void Parse_WithNoEntries_ReturnsNoMessages()
    {
        var envelope = new WhatsAppWebhookEnvelope("whatsapp_business_account", Entry: null);

        Assert.Empty(WhatsAppInboundMessageParser.Parse(envelope));
    }
}
