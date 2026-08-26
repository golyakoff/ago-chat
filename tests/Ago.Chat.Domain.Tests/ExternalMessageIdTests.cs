using Ago.Chat.Domain;

namespace Ago.Chat.Domain.Tests;

public class ExternalMessageIdTests
{
    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Rejects_EmptyOrWhitespace(string value) =>
        Assert.Throws<ArgumentException>(() => new ExternalMessageId(value));

    [Fact]
    public void Rejects_TooLong() =>
        Assert.Throws<ArgumentException>(
            () => new ExternalMessageId(new string('x', ExternalMessageId.MaxLength + 1)));

    /// <summary>
    /// The property the whole idempotency story rests on (CLAUDE.md rule 5): a redelivered provider
    /// message derives the identical <c>ClientMessageId</c>, so <c>Conversation.AddVisitorMessage</c>
    /// recognises it as the duplicate it is. If this were not a pure function, at-least-once delivery
    /// would silently become at-least-once *storage*.
    /// </summary>
    [Fact]
    public void ToClientMessageId_IsDeterministic()
    {
        var first = new ExternalMessageId("provider-msg-42").ToClientMessageId(ChannelKind.Sms);
        var second = new ExternalMessageId("provider-msg-42").ToClientMessageId(ChannelKind.Sms);

        Assert.Equal(first, second);
    }

    /// <summary>
    /// The collision the channel is mixed into the digest to prevent. A bare integer id is common
    /// across providers, and a collision here would not be a duplicate - it would be one tenant's
    /// message silently swallowed as another's retry.
    /// </summary>
    [Fact]
    public void ToClientMessageId_DiffersPerChannelForTheSameProviderId()
    {
        var id = new ExternalMessageId("12");

        Assert.NotEqual(id.ToClientMessageId(ChannelKind.Sms), id.ToClientMessageId(ChannelKind.Telegram));
    }

    [Fact]
    public void ToClientMessageId_DiffersPerProviderIdOnTheSameChannel()
    {
        Assert.NotEqual(
            new ExternalMessageId("12").ToClientMessageId(ChannelKind.Sms),
            new ExternalMessageId("13").ToClientMessageId(ChannelKind.Sms));
    }

    // A test for the U+001F separator itself was written and then deleted: with no two
    // ChannelKind members where one is a prefix of the other, no input can distinguish
    // "{kind}{sep}{value}" from "{kind}{value}", so the test passed against both and proved
    // nothing. The separator's reasoning stays in ExternalMessageId's own remarks, where it is
    // an argument rather than a claim of coverage. If a future member ever prefixes another
    // (a `Sm` alongside `Sms`), that is the moment to add the test that can finally fail.

    /// <summary>
    /// A well-formed RFC 9562 version-8 UUID, not just some deterministic 16 bytes. The version and
    /// variant nibbles are checked in their *canonical text* positions, which is what
    /// <c>bigEndian: true</c> in the derivation exists to guarantee - the default byte constructor
    /// would put them elsewhere and produce a value that is stable but not a valid UUID.
    /// </summary>
    [Fact]
    public void ToClientMessageId_IsAWellFormedVersion8Uuid()
    {
        var text = new ExternalMessageId("provider-msg-42").ToClientMessageId(ChannelKind.Max).ToString();

        Assert.Equal('8', text[14]);
        Assert.Contains(text[19], "89ab");
    }

    /// <summary>
    /// Never zero, whatever the input - <c>Guid.Empty</c> is what
    /// <c>Conversation.AddMessage</c> would treat as a real client message id shared by every inbound
    /// message ever, which would deduplicate an entire channel down to one message.
    /// </summary>
    [Fact]
    public void ToClientMessageId_IsNeverEmpty() =>
        Assert.NotEqual(Guid.Empty, new ExternalMessageId("0").ToClientMessageId(ChannelKind.Sms));
}
