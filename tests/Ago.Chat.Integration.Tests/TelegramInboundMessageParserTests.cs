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

    /// <summary>`25-148`: the exact wire form Telegram sends when a visitor opens
    /// `t.me/&lt;bot&gt;?start=4821` - this is what has to become the bare code "4821" for
    /// `ReceiveChannelMessageHandler`'s own exact-equality confirmation branch to ever match it.</summary>
    [Fact]
    public void TryParse_ForAStartDeepLinkMessage_StripsThePrefix_LeavingOnlyThePayload()
    {
        var update = MessageUpdate(updateId: 1, senderId: 12345, chatId: 999, messageId: 1, text: "/start 4821");

        var parsed = TelegramInboundMessageParser.TryParse(update);

        Assert.NotNull(parsed);
        Assert.Equal("4821", parsed.Text);
    }

    /// <summary>A visitor who manually starts the bot with no deep-link payload sends a bare "/start" -
    /// no trailing space, so nothing here matches the prefix, and the text passes through unchanged
    /// (it will simply fail to match any pending code downstream, an unremarkable outcome).</summary>
    [Fact]
    public void TryParse_ForABareStartWithNoPayload_LeavesTheTextUnchanged()
    {
        var update = MessageUpdate(updateId: 1, senderId: 12345, chatId: 999, messageId: 1, text: "/start");

        var parsed = TelegramInboundMessageParser.TryParse(update);

        Assert.NotNull(parsed);
        Assert.Equal("/start", parsed.Text);
    }

    /// <summary>A command that merely shares "/start" as a text prefix, with no separating space
    /// (Telegram never actually sends this shape, but the check itself must not be a loose
    /// <c>StartsWith("/start")</c>) must not be mistaken for a deep-link payload.</summary>
    [Fact]
    public void TryParse_ForTextStartingWithStartButNoSeparatingSpace_LeavesTheTextUnchanged()
    {
        var update = MessageUpdate(updateId: 1, senderId: 12345, chatId: 999, messageId: 1, text: "/startsomething");

        var parsed = TelegramInboundMessageParser.TryParse(update);

        Assert.NotNull(parsed);
        Assert.Equal("/startsomething", parsed.Text);
    }

    // -----------------------------------------------------------------------------------------
    // `25-151`: a shared contact - no text at all, and the `user_id` trust check that is Telegram's
    // entire mechanism for telling "the sender's own number" apart from "a forwarded address-book
    // entry."
    // -----------------------------------------------------------------------------------------

    private static TelegramUpdate ContactUpdate(
        long senderId, long chatId, long messageId, string phone, long? contactUserId,
        string? firstName = "Ada", string? lastName = "Lovelace") =>
        new(
            1,
            new TelegramMessage(
                messageId, new TelegramUser(senderId), new TelegramChat(chatId), Text: null,
                Contact: new TelegramContact(phone, firstName, lastName, contactUserId)));

    /// <summary>The item's own headline case: a contact whose `user_id` matches the sender is the
    /// visitor sharing their own number - accepted, with no text at all.</summary>
    [Fact]
    public void TryParse_AContactWhoseUserIdMatchesTheSender_IsAccepted()
    {
        var update = ContactUpdate(senderId: 12345, chatId: 999, messageId: 1, phone: "+1 555 0100", contactUserId: 12345);

        var parsed = TelegramInboundMessageParser.TryParse(update);

        Assert.NotNull(parsed);
        Assert.Null(parsed.Text);
        Assert.NotNull(parsed.Contact);
        Assert.Equal("+1 555 0100", parsed.Contact.PhoneNumber);
        Assert.Equal("Ada", parsed.Contact.FirstName);
        Assert.Equal("Lovelace", parsed.Contact.LastName);
    }

    /// <summary>The mandatory discriminator, failing: a contact whose `user_id` does not match the
    /// sender is a forwarded address-book entry, not the sender's own number - rejected, the same as
    /// no message at all (this item's own backlog text: "Telegram attaches no signature; this equality
    /// check is the entire trust").</summary>
    [Fact]
    public void TryParse_AContactWhoseUserIdDoesNotMatchTheSender_IsRejected()
    {
        var update = ContactUpdate(senderId: 12345, chatId: 999, messageId: 1, phone: "+1 555 0100", contactUserId: 99999);

        Assert.Null(TelegramInboundMessageParser.TryParse(update));
    }

    /// <summary>Telegram's own documentation states `user_id` is present only when the shared contact
    /// is a Telegram user - absent entirely (a plain phone-book contact with no Telegram account) can
    /// never satisfy an equality check against the sender's own numeric id, so it is rejected the same
    /// way a mismatched id is, never treated as trivially trusted.</summary>
    [Fact]
    public void TryParse_AContactWithNoUserId_IsRejected()
    {
        var update = ContactUpdate(senderId: 12345, chatId: 999, messageId: 1, phone: "+1 555 0100", contactUserId: null);

        Assert.Null(TelegramInboundMessageParser.TryParse(update));
    }

    [Fact]
    public void TryParse_AContactWithNoPhoneNumber_IsRejected()
    {
        var update = new TelegramUpdate(
            1,
            new TelegramMessage(
                1, new TelegramUser(12345), new TelegramChat(999), Text: null,
                Contact: new TelegramContact(PhoneNumber: null, FirstName: "Ada", LastName: "Lovelace", UserId: 12345)));

        Assert.Null(TelegramInboundMessageParser.TryParse(update));
    }

    [Fact]
    public void TryParse_AVerifiedContactWithNoName_IsStillAccepted()
    {
        var update = ContactUpdate(
            senderId: 12345, chatId: 999, messageId: 1, phone: "+1 555 0100", contactUserId: 12345,
            firstName: null, lastName: null);

        var parsed = TelegramInboundMessageParser.TryParse(update);

        Assert.NotNull(parsed);
        Assert.NotNull(parsed.Contact);
        Assert.Null(parsed.Contact.FirstName);
        Assert.Null(parsed.Contact.LastName);
    }

    // -----------------------------------------------------------------------------------------
    // `25-164`: a sent photo - this item's own confirmed root cause for "the image never arrives even
    // with a grant": before TryExtractImage existed, a caption-less photo (no `text`, and Telegram never
    // populates `text` for a photo message at all) failed the blank-body bail-out and the *entire*
    // inbound message vanished, the identical live symptom `25-161`'s own report found for MAX.
    // -----------------------------------------------------------------------------------------

    private static TelegramUpdate PhotoUpdate(
        long senderId, long chatId, long messageId, string? caption, params TelegramPhotoSize[] photo) =>
        new(
            1,
            new TelegramMessage(
                messageId, new TelegramUser(senderId), new TelegramChat(chatId), Text: null,
                Photo: photo, Caption: caption));

    /// <summary>The live symptom, reproduced: a photo with no caption used to produce nothing at all. It
    /// now produces a message with no text and a resolvable image.</summary>
    [Fact]
    public void TryParse_ACaptionLessPhoto_IsAcceptedWithNoTextAndAResolvableImage()
    {
        var update = PhotoUpdate(
            senderId: 1, chatId: 999, messageId: 1, caption: null,
            new TelegramPhotoSize("small-file-id", Width: 90, Height: 90),
            new TelegramPhotoSize("large-file-id", Width: 1280, Height: 1280));

        var parsed = TelegramInboundMessageParser.TryParse(update);

        Assert.NotNull(parsed);
        Assert.Null(parsed.Text);
        Assert.NotNull(parsed.Image);
        Assert.Equal("large-file-id", parsed.Image.FileId);
    }

    /// <summary>Telegram never populates `text` on a photo message - a captioned photo's own prose lives
    /// in the separate `caption` field (`TelegramDtos.cs`'s own remarks), confirmed against Telegram's
    /// public documentation. This parser folds it into <see cref="ParsedTelegramMessage.Text"/> so a
    /// captioned photo still produces both a plain-text message and its own attachment, independently -
    /// the same shape a captioned MAX photo already produces, reached by a different wire route.</summary>
    [Fact]
    public void TryParse_ACaptionedPhoto_CarriesBothTheCaptionAsTextAndTheImage()
    {
        var update = PhotoUpdate(
            senderId: 1, chatId: 999, messageId: 1, caption: "look at this",
            new TelegramPhotoSize("only-file-id", Width: 800, Height: 600));

        var parsed = TelegramInboundMessageParser.TryParse(update);

        Assert.NotNull(parsed);
        Assert.Equal("look at this", parsed.Text);
        Assert.NotNull(parsed.Image);
        Assert.Equal("only-file-id", parsed.Image.FileId);
    }

    /// <summary>Telegram's own documentation describes the `photo` array as ordered smallest-to-largest
    /// but does not make that an enforced schema guarantee - <see cref="TelegramInboundMessageParser"/>'s
    /// own `TryExtractImage` picks the highest resolution by `width` explicitly, proven here by handing
    /// it the sizes in descending, not ascending, order.</summary>
    [Fact]
    public void TryParse_APhotoWithSizesOutOfDocumentedOrder_StillPicksTheHighestResolution()
    {
        var update = PhotoUpdate(
            senderId: 1, chatId: 999, messageId: 1, caption: null,
            new TelegramPhotoSize("largest-file-id", Width: 1280, Height: 1280),
            new TelegramPhotoSize("smallest-file-id", Width: 90, Height: 90));

        var parsed = TelegramInboundMessageParser.TryParse(update);

        Assert.NotNull(parsed);
        Assert.NotNull(parsed.Image);
        Assert.Equal("largest-file-id", parsed.Image.FileId);
    }

    [Fact]
    public void TryParse_AnEmptyPhotoArray_IsTreatedAsNoImage()
    {
        var update = PhotoUpdate(senderId: 1, chatId: 999, messageId: 1, caption: "no sizes at all");

        var parsed = TelegramInboundMessageParser.TryParse(update);

        Assert.NotNull(parsed);
        Assert.Equal("no sizes at all", parsed.Text);
        Assert.Null(parsed.Image);
    }

    [Fact]
    public void TryParse_NoPhotoAndNoTextAndNoContact_ReturnsNull()
    {
        var update = new TelegramUpdate(
            1, new TelegramMessage(1, new TelegramUser(1), new TelegramChat(999), Text: null));

        Assert.Null(TelegramInboundMessageParser.TryParse(update));
    }
}
