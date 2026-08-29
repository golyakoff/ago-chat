namespace Ago.Chat.Domain.Tests;

/// <summary>`20-07`: the one central reply-parsing function, proven kind-agnostic - see
/// <see cref="ChoiceReplyTextResolver"/>'s own remarks on why it must not special-case a
/// <see cref="MessageContentKind"/>.</summary>
public class ChoiceReplyTextResolverTests
{
    private static readonly IReadOnlyList<MessageAction> Actions =
    [
        new MessageAction("Monday 10:00", "slot-1"),
        new MessageAction("Monday 14:00", "slot-2"),
        new MessageAction("Tuesday 09:00", "slot-3"),
    ];

    [Theory]
    [InlineData("1", "slot-1")]
    [InlineData("2", "slot-2")]
    [InlineData("3", "slot-3")]
    public void Resolve_WithAValidOneBasedIndex_ReturnsThatActionsValue(string reply, string expected) =>
        Assert.Equal(expected, ChoiceReplyTextResolver.Resolve(reply, Actions));

    [Fact]
    public void Resolve_TrimsWhitespace() =>
        Assert.Equal("slot-1", ChoiceReplyTextResolver.Resolve("  1  ", Actions));

    [Theory]
    [InlineData("0")]
    [InlineData("4")]
    [InlineData("-1")]
    [InlineData("999")]
    public void Resolve_WithAnOutOfRangeIndex_ReturnsNull(string reply) =>
        Assert.Null(ChoiceReplyTextResolver.Resolve(reply, Actions));

    [Theory]
    [InlineData("one")]
    [InlineData("1.5")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1 2")]
    public void Resolve_WithNonNumericOrMalformedText_ReturnsNull(string reply) =>
        Assert.Null(ChoiceReplyTextResolver.Resolve(reply, Actions));

    [Fact]
    public void Resolve_WithNoActions_ReturnsNull() =>
        Assert.Null(ChoiceReplyTextResolver.Resolve("1", []));

    /// <summary>No fuzzy matching against a label's text - the item's own explicit constraint.</summary>
    [Fact]
    public void Resolve_DoesNotMatchByLabelText() =>
        Assert.Null(ChoiceReplyTextResolver.Resolve("Monday 10:00", Actions));
}
