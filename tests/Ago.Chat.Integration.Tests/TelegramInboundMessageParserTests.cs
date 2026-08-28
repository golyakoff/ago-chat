using Ago.Chat.Infrastructure.Telegram;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `14-07`: <see cref="TelegramInboundMessageParser"/> is a pure function with no infrastructure
/// dependency of its own - it lives here, in <c>Ago.Chat.Integration.Tests</c>, for the identical
/// pragmatic reason <see cref="MaxInboundMessageParserTests"/> gives for its own placement: this
/// project already references <c>Ago.Chat.Infrastructure.Telegram</c>
/// (<see cref="TelegramApiClientTests"/>'s own need), and a sixth test project for one pure-function
/// class would not earn its keep.
///
/// <para>Unlike <c>MaxInboundMessageParserTests</c>, the field names asserted against here are taken
/// directly from Telegram's own public Bot API documentation (core.telegram.org/bots/api), not
/// reconstructed from third-party write-ups - see <c>TelegramDtos.cs</c>'s own remarks.</para>
/// </summary>
public class TelegramInboundMessageParserTests
{
    private static TelegramUpdate MessageUpdate(
        long updateId, long senderId, long chatId, long messageId, string? text) =>
        new(updateId, new TelegramMessage(messageId, new TelegramUser(senderId), new TelegramChat(chatId), text));

    [Fact]
    public void TryParse_ForAMessageUpdate_ExtractsChatIdSenderTextAndCompositeId()
    {
        var update = MessageUpdate(updateId: 1, senderId: 12345, chatId: 999, messageId: 42, text: "hello there");

        var parsed = TelegramInboundMessageParser.TryParse(update);

        Assert.NotNull(parsed);
        Assert.Equal(999, parsed.ChatId);
        Assert.Equal(12345, parsed.SenderId);
        Assert.Equal("hello there", parsed.Text);
        Assert.Equal("999:42", parsed.ExternalMessageId);
    }

    /// <summary>The finding <see cref="TelegramInboundMessageParser"/>'s own remarks describe: Telegram's
    /// <c>message_id</c> is only unique within one chat, so two different chats reusing the same
    /// <c>message_id</c> must not produce the same external message id - proven here directly, rather
    /// than trusted from the parser's own comment.</summary>
    [Fact]
    public void TryParse_TwoDifferentChatsWithTheSameMessageId_ProduceDifferentExternalMessageIds()
    {
        var firstChat = MessageUpdate(updateId: 1, senderId: 1, chatId: 111, messageId: 7, text: "hi from chat one");
        var secondChat = MessageUpdate(updateId: 2, senderId: 2, chatId: 222, messageId: 7, text: "hi from chat two");

        var parsedFirst = TelegramInboundMessageParser.TryParse(firstChat);
        var parsedSecond = TelegramInboundMessageParser.TryParse(secondChat);

        Assert.NotNull(parsedFirst);
        Assert.NotNull(parsedSecond);
        Assert.NotEqual(parsedFirst.ExternalMessageId, parsedSecond.ExternalMessageId);
    }

    [Fact]
    public void TryParse_ForAnUpdateWithNoMessage_ReturnsNull()
    {
        var update = new TelegramUpdate(1, null);

        Assert.Null(TelegramInboundMessageParser.TryParse(update));
    }

    [Fact]
    public void TryParse_WithNoSenderId_ReturnsNull()
    {
        var update = new TelegramUpdate(1, new TelegramMessage(1, new TelegramUser(null), new TelegramChat(1), "hi"));

        Assert.Null(TelegramInboundMessageParser.TryParse(update));
    }

    [Fact]
    public void TryParse_WithNoChatId_ReturnsNull()
    {
        var update = new TelegramUpdate(1, new TelegramMessage(1, new TelegramUser(1), new TelegramChat(null), "hi"));

        Assert.Null(TelegramInboundMessageParser.TryParse(update));
    }

    [Fact]
    public void TryParse_WithNoMessageId_ReturnsNull()
    {
        var update = new TelegramUpdate(1, new TelegramMessage(null, new TelegramUser(1), new TelegramChat(1), "hi"));

        Assert.Null(TelegramInboundMessageParser.TryParse(update));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_WithNoText_ReturnsNull(string? text)
    {
        var update = MessageUpdate(updateId: 1, senderId: 1, chatId: 1, messageId: 1, text: text);

        Assert.Null(TelegramInboundMessageParser.TryParse(update));
    }
}
