using System.Text.Json;
using Ago.Chat.Infrastructure.Vk;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `14-08`: <see cref="VkInboundMessageParser"/> is a pure function with no infrastructure dependency of
/// its own - <see cref="MaxInboundMessageParserTests"/>'s own precedent for living in
/// <c>Ago.Chat.Integration.Tests</c> rather than a dedicated <c>Ago.Chat.Infrastructure.Vk.Tests</c>
/// project, for the identical pragmatic reason that class states for itself.
///
/// <para><b>Honesty note, repeated from <c>VkDtos.cs</c> and unlike <c>MaxInboundMessageParserTests</c>'
/// own version of this note.</b> The envelope-level shapes these tests assert against
/// (<c>type</c>/<c>object</c>/<c>secret</c>/<c>group_id</c>) came straight from VK's own official SDK
/// source, not a guess - the one part still unconfirmed against a real payload is the nesting inside
/// <c>object</c> for a <c>message_new</c> event specifically (<c>VkDtos.cs</c>'s own remarks on
/// <see cref="VkMessageNewObject"/>). What is proven here is that the parser's own logic (recognise
/// <c>message_new</c>, ignore everything else, filter a community's own outgoing echo, extract
/// peer/sender/text/id, fall back sanely when <c>id</c> is absent) behaves correctly against the shape
/// this item assumed; whether that specific nesting is VK's real one is exactly what a real community
/// token would settle.</para>
/// </summary>
public class VkInboundMessageParserTests
{
    private static VkCallbackEvent MessageNew(
        long fromId, string text, long? id = 1001, long? date = 1_700_000_000, long peerId = 999, int out_ = 0)
    {
        // A Dictionary, not an anonymous type - `out` is a C# keyword and cannot be an anonymous type's
        // own member name, but is exactly VK's real wire field name (VkMessage.Out's own remarks).
        var payload = JsonSerializer.SerializeToElement(new
        {
            message = new Dictionary<string, object?>
            {
                ["id"] = id,
                ["date"] = date,
                ["from_id"] = fromId,
                ["peer_id"] = peerId,
                ["text"] = text,
                ["out"] = out_,
            },
        });

        return new VkCallbackEvent("message_new", GroupId: 1, Secret: "s", EventId: "1", Object: payload);
    }

    [Fact]
    public void TryParse_ForAMessageNewEvent_ExtractsPeerIdFromIdTextAndId()
    {
        var callbackEvent = MessageNew(fromId: 12345, text: "hello there", id: 1001, peerId: 999);

        var parsed = VkInboundMessageParser.TryParse(callbackEvent);

        Assert.NotNull(parsed);
        Assert.Equal(999, parsed.PeerId);
        Assert.Equal(12345, parsed.FromId);
        Assert.Equal("hello there", parsed.Text);
        Assert.Equal("1001", parsed.ExternalMessageId);
    }

    /// <summary>The one rule with no MAX/Telegram equivalent - <see cref="VkInboundMessageParser"/>'s own
    /// remarks on why an operator's own reply, echoed back by VK's Callback API as a fresh
    /// <c>message_new</c> event, must never be treated as a new inbound visitor message.</summary>
    [Fact]
    public void TryParse_ForACommunitysOwnOutgoingMessage_ReturnsNull()
    {
        var callbackEvent = MessageNew(fromId: 1, text: "an operator's own reply", out_: 1);

        Assert.Null(VkInboundMessageParser.TryParse(callbackEvent));
    }

    [Theory]
    [InlineData("wall_post_new")]
    [InlineData("group_join")]
    [InlineData(null)]
    public void TryParse_ForAnyOtherEventType_ReturnsNull(string? type)
    {
        var payload = JsonSerializer.SerializeToElement(new { message = new { id = 1, from_id = 1, peer_id = 1, text = "hi", out_ = 0 } });
        var callbackEvent = new VkCallbackEvent(type, GroupId: 1, Secret: "s", EventId: "1", Object: payload);

        Assert.Null(VkInboundMessageParser.TryParse(callbackEvent));
    }

    [Fact]
    public void TryParse_WithNoObject_ReturnsNull()
    {
        var callbackEvent = new VkCallbackEvent("message_new", GroupId: 1, Secret: "s", EventId: "1", Object: null);

        Assert.Null(VkInboundMessageParser.TryParse(callbackEvent));
    }

    [Fact]
    public void TryParse_WithNoMessageInsideObject_ReturnsNull()
    {
        var payload = JsonSerializer.SerializeToElement(new { client_info = new { } });
        var callbackEvent = new VkCallbackEvent("message_new", GroupId: 1, Secret: "s", EventId: "1", Object: payload);

        Assert.Null(VkInboundMessageParser.TryParse(callbackEvent));
    }

    [Fact]
    public void TryParse_WithNoFromId_ReturnsNull()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            message = new Dictionary<string, object?> { ["id"] = 1, ["peer_id"] = 1, ["text"] = "hi", ["out"] = 0 },
        });
        var callbackEvent = new VkCallbackEvent("message_new", GroupId: 1, Secret: "s", EventId: "1", Object: payload);

        Assert.Null(VkInboundMessageParser.TryParse(callbackEvent));
    }

    [Fact]
    public void TryParse_WithNoPeerId_ReturnsNull()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            message = new Dictionary<string, object?> { ["id"] = 1, ["from_id"] = 1, ["text"] = "hi", ["out"] = 0 },
        });
        var callbackEvent = new VkCallbackEvent("message_new", GroupId: 1, Secret: "s", EventId: "1", Object: payload);

        Assert.Null(VkInboundMessageParser.TryParse(callbackEvent));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_WithNoText_ReturnsNull(string? text)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            message = new Dictionary<string, object?> { ["id"] = 1, ["from_id"] = 1, ["peer_id"] = 1, ["text"] = text, ["out"] = 0 },
        });
        var callbackEvent = new VkCallbackEvent("message_new", GroupId: 1, Secret: "s", EventId: "1", Object: payload);

        Assert.Null(VkInboundMessageParser.TryParse(callbackEvent));
    }

    /// <summary>The documented fallback for a field VK's own SDK source did not confirm is always
    /// present on every message - <see cref="MaxInboundMessageParserTests"/>' own identical trade-off for
    /// MAX's `body.mid`.</summary>
    [Fact]
    public void TryParse_WithNoOrZeroId_FallsBackToAPeerAndDateDerivedId()
    {
        var callbackEvent = MessageNew(fromId: 555, text: "hi", id: 0, date: 42, peerId: 777);

        var parsed = VkInboundMessageParser.TryParse(callbackEvent);

        Assert.NotNull(parsed);
        Assert.Equal("777:42", parsed.ExternalMessageId);
    }
}
