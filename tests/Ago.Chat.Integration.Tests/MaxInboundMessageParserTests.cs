using System.Security.Cryptography;
using System.Text;
using Ago.Chat.Infrastructure.MaxBot;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `14-02`: <see cref="MaxInboundMessageParser"/> is a pure function with no infrastructure dependency
/// of its own - it lives here, in <c>Ago.Chat.Integration.Tests</c>, rather than a new
/// <c>Ago.Chat.Infrastructure.MaxBot.Tests</c> project purely as a pragmatic choice: this project
/// already references <c>Ago.Chat.Infrastructure.MaxBot</c> (<see cref="MaxChannelAdapterResilienceTests"/>'s
/// own need), and a fifth test project for one pure-function class would not earn its keep. If a second
/// class in that assembly ever needs its own fast unit tests, splitting this file out is the moment to
/// do it, not before.
///
/// <para><b>Honesty note, repeated from <c>MaxDtos.cs</c>.</b> The exact JSON field names these tests
/// assert against are this item's own best-effort reconstruction of MAX's update envelope from public
/// documentation and third-party write-ups, not a captured real payload - no bot token was available
/// while this item was built. What is proven here is that the parser's own logic (recognise
/// <c>message_created</c>, ignore everything else, extract sender/text/id, fall back sanely when
/// <c>body.mid</c> is absent) behaves correctly against the shape this item assumed; whether that shape
/// is MAX's actual one is exactly what a real bot token would settle.</para>
/// </summary>
public class MaxInboundMessageParserTests
{
    private static MaxUpdate MessageCreated(
        long senderId, string text, string? mid = "provider-mid-1", long? timestamp = 1_700_000_000_000, long chatId = 999) =>
        new("message_created", timestamp, new MaxIncomingMessage(new MaxUser(senderId), new MaxRecipient(chatId), new MaxMessageBody(mid, text), timestamp));

    /// <summary>Found live, 2026-08-28: `chat_id`, not `sender.user_id`, is what every outbound reply
    /// must target - see `MaxInboundMessageParser.TryParse`'s own remarks on the real `chat.not.found`
    /// refusal that exposed this. `SenderId` is still extracted and asserted here (nothing this parser
    /// does should regress it), but it is `ChatId` that becomes the channel identity's own address.</summary>
    [Fact]
    public void TryParse_ForAMessageCreatedUpdate_ExtractsChatIdSenderTextAndId()
    {
        var update = MessageCreated(12345, "hello there", "mid-abc", chatId: 999);

        var parsed = MaxInboundMessageParser.TryParse(update);

        Assert.NotNull(parsed);
        Assert.Equal(999, parsed.ChatId);
        Assert.Equal(12345, parsed.SenderId);
        Assert.Equal("hello there", parsed.Text);
        Assert.Equal("mid-abc", parsed.ExternalMessageId);
    }

    [Theory]
    [InlineData("bot_started")]
    [InlineData("chat_title_changed")]
    [InlineData(null)]
    public void TryParse_ForAnyOtherUpdateType_ReturnsNull(string? updateType)
    {
        var update = new MaxUpdate(updateType, 1, new MaxIncomingMessage(new MaxUser(1), new MaxRecipient(1), new MaxMessageBody("m", "hi"), 1));

        Assert.Null(MaxInboundMessageParser.TryParse(update));
    }

    [Fact]
    public void TryParse_WithNoSenderUserId_ReturnsNull()
    {
        var update = new MaxUpdate("message_created", 1, new MaxIncomingMessage(new MaxUser(null), new MaxRecipient(1), new MaxMessageBody("m", "hi"), 1));

        Assert.Null(MaxInboundMessageParser.TryParse(update));
    }

    /// <summary>The other half of the same live finding – a message with a sender but no chat_id
    /// has nowhere this system could ever reply to, even once it understands everything else about
    /// it, so this is refused the identical way a missing sender already was.</summary>
    [Fact]
    public void TryParse_WithNoRecipientChatId_ReturnsNull()
    {
        var update = new MaxUpdate("message_created", 1, new MaxIncomingMessage(new MaxUser(1), new MaxRecipient(null), new MaxMessageBody("m", "hi"), 1));

        Assert.Null(MaxInboundMessageParser.TryParse(update));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_WithNoText_ReturnsNull(string? text)
    {
        var update = MessageCreated(1, text!);

        Assert.Null(MaxInboundMessageParser.TryParse(update));
    }

    /// <summary>The documented fallback for a field the public documentation never confirmed MAX
    /// always sends - see this class's own honesty note. Derived from chat_id, not sender_id, now
    /// that chat_id is the field the rest of this parser actually keys on.</summary>
    [Fact]
    public void TryParse_WithNoMid_FallsBackToAChatAndTimestampDerivedId()
    {
        var update = MessageCreated(555, "hi", mid: null, timestamp: 42, chatId: 777);

        var parsed = MaxInboundMessageParser.TryParse(update);

        Assert.NotNull(parsed);
        Assert.Equal("777:42", parsed.ExternalMessageId);
    }

    [Fact]
    public void TryParse_WithNoMessageObject_ReturnsNull()
    {
        var update = new MaxUpdate("message_created", 1, null);

        Assert.Null(MaxInboundMessageParser.TryParse(update));
    }

    // -----------------------------------------------------------------------------------------
    // `25-151`: a shared contact attachment - no text at all, and the HMAC-SHA256 hash check that is
    // this item's own best-effort reconstruction of MAX's substitute for Telegram's `user_id` equality
    // (this file's own honesty note above, restated for this one addition: field names and the exact
    // digest encoding are not confirmed against a live capture).
    // -----------------------------------------------------------------------------------------

    private const string BotToken = "test-bot-token";

    private static string ComputeHash(string vcfInfo, string botToken) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(botToken), Encoding.UTF8.GetBytes(vcfInfo)));

    private static MaxUpdate ContactUpdate(
        long senderId, long chatId, string vcfInfo, string? hash, string? phone = "+1 555 0100",
        string? firstName = "Ada", string? lastName = "Lovelace") =>
        new(
            "message_created", 1_700_000_000_000,
            new MaxIncomingMessage(
                new MaxUser(senderId), new MaxRecipient(chatId),
                new MaxMessageBody(
                    Mid: "provider-mid-1", Text: null,
                    Attachments:
                    [
                        new MaxAttachment(
                            "contact",
                            new MaxAttachmentPayload(
                                vcfInfo, new MaxContactInfo(senderId, phone, firstName, lastName), hash)),
                    ]),
                1_700_000_000_000));

    /// <summary>The item's own headline case for MAX: a contact attachment whose hash verifies against
    /// this site's own bot token is accepted, with no text at all.</summary>
    [Fact]
    public void TryParse_AContactAttachmentWhoseHashVerifies_IsAccepted()
    {
        const string vcfInfo = "BEGIN:VCARD\nTEL:+15550100\nEND:VCARD";
        var update = ContactUpdate(senderId: 1, chatId: 999, vcfInfo, hash: ComputeHash(vcfInfo, BotToken));

        var parsed = MaxInboundMessageParser.TryParse(update, BotToken);

        Assert.NotNull(parsed);
        Assert.Null(parsed.Text);
        Assert.NotNull(parsed.Contact);
        Assert.Equal("+1 555 0100", parsed.Contact.Phone);
        Assert.Equal("Ada", parsed.Contact.FirstName);
        Assert.Equal("Lovelace", parsed.Contact.LastName);
    }

    /// <summary>Case-insensitive on purpose - see this method's own honesty note on the digest's
    /// unconfirmed exact encoding.</summary>
    [Fact]
    public void TryParse_AVerifyingHash_IsAcceptedRegardlessOfLetterCasing()
    {
        const string vcfInfo = "BEGIN:VCARD\nTEL:+15550100\nEND:VCARD";
        var update = ContactUpdate(senderId: 1, chatId: 999, vcfInfo, hash: ComputeHash(vcfInfo, BotToken).ToLowerInvariant());

        Assert.NotNull(MaxInboundMessageParser.TryParse(update, BotToken));
    }

    /// <summary>The mandatory discriminator, failing: a hash that does not verify against this site's
    /// own bot token is rejected - the same as no contact attachment at all.</summary>
    [Fact]
    public void TryParse_AContactAttachmentWithAWrongHash_IsRejected()
    {
        const string vcfInfo = "BEGIN:VCARD\nTEL:+15550100\nEND:VCARD";
        var update = ContactUpdate(senderId: 1, chatId: 999, vcfInfo, hash: ComputeHash(vcfInfo, "a-different-token"));

        Assert.Null(MaxInboundMessageParser.TryParse(update, BotToken));
    }

    /// <summary>A caller with no bot token available (the default parameter every ordinary-text call
    /// site before this item still uses) can never verify a real HMAC - "no contact" for any update
    /// that happens to carry one, never a false accept.</summary>
    [Fact]
    public void TryParse_WithNoBotTokenSupplied_RejectsEvenAGenuinelyValidHash()
    {
        const string vcfInfo = "BEGIN:VCARD\nTEL:+15550100\nEND:VCARD";
        var update = ContactUpdate(senderId: 1, chatId: 999, vcfInfo, hash: ComputeHash(vcfInfo, BotToken));

        Assert.Null(MaxInboundMessageParser.TryParse(update));
    }

    [Fact]
    public void TryParse_AContactAttachmentWithNoHash_IsRejected()
    {
        const string vcfInfo = "BEGIN:VCARD\nTEL:+15550100\nEND:VCARD";
        var update = ContactUpdate(senderId: 1, chatId: 999, vcfInfo, hash: null);

        Assert.Null(MaxInboundMessageParser.TryParse(update, BotToken));
    }

    [Fact]
    public void TryParse_AContactAttachmentWithNoPhone_IsRejected()
    {
        const string vcfInfo = "BEGIN:VCARD\nTEL:+15550100\nEND:VCARD";
        var update = ContactUpdate(senderId: 1, chatId: 999, vcfInfo, hash: ComputeHash(vcfInfo, BotToken), phone: null);

        Assert.Null(MaxInboundMessageParser.TryParse(update, BotToken));
    }

    /// <summary>`25-161`: <c>"image"</c> is no longer one of the types this parser ignores outright -
    /// see the dedicated image tests below for that. A type this parser genuinely has no use case for
    /// at all (a sticker) is the honest stand-in for "not a type this parser understands."</summary>
    [Fact]
    public void TryParse_AnAttachmentOfAnUnhandledType_IsIgnored()
    {
        var update = new MaxUpdate(
            "message_created", 1,
            new MaxIncomingMessage(
                new MaxUser(1), new MaxRecipient(999),
                new MaxMessageBody(
                    Mid: "m", Text: null,
                    Attachments: [new MaxAttachment("sticker", Payload: null)]),
                1));

        Assert.Null(MaxInboundMessageParser.TryParse(update, BotToken));
    }

    // -----------------------------------------------------------------------------------------
    // `25-161`: a sent photo - this item's own confirmed root cause for "the image never arrives even
    // once granted." Before this item, TryParse had no branch for MaxAttachmentPayload.Url at all, so a
    // caption-less photo (no `text`) failed the blank-body bail-out and the *entire* inbound message
    // vanished - not merely its attachment.
    // -----------------------------------------------------------------------------------------

    private static MaxUpdate ImageUpdate(long senderId, long chatId, string? text, string? url) =>
        new(
            "message_created", 1_700_000_000_000,
            new MaxIncomingMessage(
                new MaxUser(senderId), new MaxRecipient(chatId),
                new MaxMessageBody(
                    Mid: "provider-mid-1", Text: text,
                    Attachments: [new MaxAttachment("image", new MaxAttachmentPayload(null, null, null, Url: url))]),
                1_700_000_000_000));

    /// <summary>The live symptom, reproduced: a photo with no caption used to produce nothing at all.
    /// It now produces a message with no text and a resolvable image.</summary>
    [Fact]
    public void TryParse_ACaptionLessPhoto_IsAcceptedWithNoTextAndAResolvableImage()
    {
        var update = ImageUpdate(senderId: 1, chatId: 999, text: null, url: "https://cdn.max.example/photo.jpg");

        var parsed = MaxInboundMessageParser.TryParse(update);

        Assert.NotNull(parsed);
        Assert.Null(parsed.Text);
        Assert.NotNull(parsed.Image);
        Assert.Equal("https://cdn.max.example/photo.jpg", parsed.Image.Url);
    }

    /// <summary>A captioned photo carries both, independently - MAX's own documentation gives no reason
    /// to treat text and an image attachment as mutually exclusive the way Telegram's text/contact split
    /// is (this class's own ParsedMaxMessage remarks).</summary>
    [Fact]
    public void TryParse_ACaptionedPhoto_CarriesBothTextAndImage()
    {
        var update = ImageUpdate(senderId: 1, chatId: 999, text: "look at this", url: "https://cdn.max.example/photo.jpg");

        var parsed = MaxInboundMessageParser.TryParse(update);

        Assert.NotNull(parsed);
        Assert.Equal("look at this", parsed.Text);
        Assert.NotNull(parsed.Image);
        Assert.Equal("https://cdn.max.example/photo.jpg", parsed.Image.Url);
    }

    /// <summary>`25-161`: MAX's own token-only reference this codebase has no confirmed way to resolve
    /// to bytes (MaxAttachmentPayload's own remarks) - a caption-less photo whose payload carries no
    /// `url` degrades to the same "nothing this parser understood" outcome a caption-less message
    /// always has, rather than a guess.</summary>
    [Fact]
    public void TryParse_ACaptionLessPhotoWithNoUrl_IsIgnored()
    {
        var update = new MaxUpdate(
            "message_created", 1,
            new MaxIncomingMessage(
                new MaxUser(1), new MaxRecipient(999),
                new MaxMessageBody(
                    Mid: "m", Text: null,
                    Attachments: [new MaxAttachment("image", new MaxAttachmentPayload(null, null, null, Token: "opaque-token"))]),
                1));

        Assert.Null(MaxInboundMessageParser.TryParse(update));
    }

    /// <summary>A captioned photo with no resolvable URL still keeps its caption - the same "this parser
    /// acts on whichever fields it actually understood" posture the rest of this file already
    /// establishes for a rejected contact.</summary>
    [Fact]
    public void TryParse_ACaptionedPhotoWithNoUrl_KeepsTheCaptionButNoImage()
    {
        var update = ImageUpdate(senderId: 1, chatId: 999, text: "look at this", url: null);

        var parsed = MaxInboundMessageParser.TryParse(update);

        Assert.NotNull(parsed);
        Assert.Equal("look at this", parsed.Text);
        Assert.Null(parsed.Image);
    }
}
