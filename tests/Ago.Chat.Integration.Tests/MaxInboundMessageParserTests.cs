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
    private static MaxUpdate MessageCreated(long senderId, string text, string? mid = "provider-mid-1", long? timestamp = 1_700_000_000_000) =>
        new("message_created", timestamp, new MaxIncomingMessage(new MaxUser(senderId), new MaxRecipient(999), new MaxMessageBody(mid, text), timestamp));

    [Fact]
    public void TryParse_ForAMessageCreatedUpdate_ExtractsSenderTextAndId()
    {
        var update = MessageCreated(12345, "hello there", "mid-abc");

        var parsed = MaxInboundMessageParser.TryParse(update);

        Assert.NotNull(parsed);
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
    /// always sends - see this class's own honesty note.</summary>
    [Fact]
    public void TryParse_WithNoMid_FallsBackToASenderAndTimestampDerivedId()
    {
        var update = MessageCreated(555, "hi", mid: null, timestamp: 42);

        var parsed = MaxInboundMessageParser.TryParse(update);

        Assert.NotNull(parsed);
        Assert.Equal("555:42", parsed.ExternalMessageId);
    }

    [Fact]
    public void TryParse_WithNoMessageObject_ReturnsNull()
    {
        var update = new MaxUpdate("message_created", 1, null);

        Assert.Null(MaxInboundMessageParser.TryParse(update));
    }
}
