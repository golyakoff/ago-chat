using Ago.Chat.Infrastructure.Avito;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `14-11`: <see cref="AvitoInboundMessageParser"/> is a pure function with no infrastructure dependency
/// of its own - <see cref="VkInboundMessageParserTests"/>'s own precedent for living in
/// <c>Ago.Chat.Integration.Tests</c> rather than a dedicated <c>Ago.Chat.Infrastructure.Avito.Tests</c>
/// project, for the identical pragmatic reason that class states for itself.
///
/// <para><b>Honesty note, repeated from <c>AvitoDtos.cs</c>.</b> Every field name asserted against here
/// came directly from Avito's own published OpenAPI schema, not a guess - what is proven here is that the
/// parser's own logic (recognise a text message, skip a non-message payload, skip the seller's own
/// outgoing echo inferred from <c>author_id == user_id</c>, skip an <c>a2u</c> system chat, extract
/// chat/text/id) behaves correctly against the shape this item's own research confirmed; whether a real
/// Avito delivery matches it exactly is exactly what a real seller account and a real message would
/// settle.</para>
/// </summary>
public class AvitoInboundMessageParserTests
{
    private static AvitoWebhookEnvelope MessageEnvelope(
        long? authorId, long? userId, string text, string? id = "msg-1", string chatId = "chat-1",
        string chatType = AvitoChatTypes.Item, string messageType = AvitoMessageTypes.Text) =>
        new("env-1", "v1.1", 123,
            new AvitoWebhookPayload("message",
                new AvitoWebhookMessage(id, chatId, chatType, authorId, userId, ItemId: 555, messageType,
                    new AvitoMessageContent(text), Created: 123)));

    [Fact]
    public void Parse_ForAnInboundTextMessage_ExtractsChatIdTextAndId()
    {
        var envelope = MessageEnvelope(authorId: 111, userId: 94235311, text: "здравствуйте, актуально?", id: "msg-42", chatId: "chat-42");

        var parsed = AvitoInboundMessageParser.Parse(envelope);

        Assert.NotNull(parsed);
        Assert.Equal("chat-42", parsed.ChatId);
        Assert.Equal("здравствуйте, актуально?", parsed.Text);
        Assert.Equal("msg-42", parsed.ExternalMessageId);
        Assert.Equal(94235311, parsed.WebhookOwnerUserId);
    }

    [Theory]
    [InlineData(AvitoChatTypes.Item)]
    [InlineData(AvitoChatTypes.User)]
    public void Parse_ForBothRealBuyerChatTypes_Succeeds(string chatType)
    {
        var envelope = MessageEnvelope(authorId: 111, userId: 94235311, text: "hi", chatType: chatType);

        Assert.NotNull(AvitoInboundMessageParser.Parse(envelope));
    }

    /// <summary>This item's own scope cut - a chat with Avito itself is not a customer conversation
    /// (<see cref="AvitoInboundMessageParser"/>'s own remarks).</summary>
    [Fact]
    public void Parse_ForAnAvitoSystemChat_ReturnsNull()
    {
        var envelope = MessageEnvelope(authorId: 111, userId: 94235311, text: "hi", chatType: AvitoChatTypes.Avito);

        Assert.Null(AvitoInboundMessageParser.Parse(envelope));
    }

    /// <summary>The one rule with no direct MAX/Telegram equivalent - <see cref="AvitoInboundMessageParser"/>'s
    /// own remarks on why the seller's own outgoing message (author_id == user_id) must never be treated
    /// as a new inbound visitor message, the Avito-shaped version of the hazard VK's <c>message.out</c>
    /// solves for VK.</summary>
    [Fact]
    public void Parse_ForTheSellersOwnOutgoingMessage_ReturnsNull()
    {
        var envelope = MessageEnvelope(authorId: 94235311, userId: 94235311, text: "an operator's own reply");

        Assert.Null(AvitoInboundMessageParser.Parse(envelope));
    }

    [Fact]
    public void Parse_WithAPayloadTypeOtherThanMessage_ReturnsNull()
    {
        var envelope = new AvitoWebhookEnvelope("env-1", "v1.1", 123,
            new AvitoWebhookPayload("something_else", new AvitoWebhookMessage(
                "msg-1", "chat-1", AvitoChatTypes.Item, 111, 94235311, 555, AvitoMessageTypes.Text,
                new AvitoMessageContent("hi"), 123)));

        Assert.Null(AvitoInboundMessageParser.Parse(envelope));
    }

    [Fact]
    public void Parse_WithNoPayloadValue_ReturnsNull()
    {
        var envelope = new AvitoWebhookEnvelope("env-1", "v1.1", 123, new AvitoWebhookPayload("message", null));

        Assert.Null(AvitoInboundMessageParser.Parse(envelope));
    }

    [Theory]
    [InlineData("image")]
    [InlineData("system")]
    [InlineData("voice")]
    [InlineData("location")]
    [InlineData("call")]
    public void Parse_ForANonTextMessageType_ReturnsNull(string messageType)
    {
        var envelope = MessageEnvelope(authorId: 111, userId: 94235311, text: "irrelevant", messageType: messageType);

        Assert.Null(AvitoInboundMessageParser.Parse(envelope));
    }

    [Fact]
    public void Parse_WithNoChatId_ReturnsNull()
    {
        var envelope = new AvitoWebhookEnvelope("env-1", "v1.1", 123,
            new AvitoWebhookPayload("message", new AvitoWebhookMessage(
                "msg-1", null, AvitoChatTypes.Item, 111, 94235311, 555, AvitoMessageTypes.Text,
                new AvitoMessageContent("hi"), 123)));

        Assert.Null(AvitoInboundMessageParser.Parse(envelope));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_WithNoText_ReturnsNull(string? text)
    {
        var envelope = new AvitoWebhookEnvelope("env-1", "v1.1", 123,
            new AvitoWebhookPayload("message", new AvitoWebhookMessage(
                "msg-1", "chat-1", AvitoChatTypes.Item, 111, 94235311, 555, AvitoMessageTypes.Text,
                new AvitoMessageContent(text), 123)));

        Assert.Null(AvitoInboundMessageParser.Parse(envelope));
    }

    [Fact]
    public void Parse_WithNoExternalMessageId_ReturnsNull()
    {
        var envelope = MessageEnvelope(authorId: 111, userId: 94235311, text: "hi", id: null);

        Assert.Null(AvitoInboundMessageParser.Parse(envelope));
    }
}
