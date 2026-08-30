namespace Ago.Chat.Domain.Tests;

public class LinkIdentityCommandMatcherTests
{
    [Theory]
    [InlineData("/linkidentity telegram")]
    [InlineData("linkidentity telegram")]
    [InlineData("LINKIDENTITY TELEGRAM")]
    [InlineData("  /linkidentity   telegram  ")]
    public void Match_ARealCommandWithARealChannelKind_ReturnsMatched(string body)
    {
        var result = LinkIdentityCommandMatcher.Match(body);

        Assert.Equal(LinkIdentityCommandMatch.Matched, result.Match);
        Assert.Equal(ChannelKind.Telegram, result.Kind);
    }

    [Theory]
    [InlineData("hello there")]
    [InlineData("I'd like to link my telegram")]
    [InlineData("please linkidentity telegram")]
    [InlineData("")]
    [InlineData("   ")]
    public void Match_OrdinaryConversation_ReturnsNotACommand(string body)
    {
        var result = LinkIdentityCommandMatcher.Match(body);

        Assert.Equal(LinkIdentityCommandMatch.NotACommand, result.Match);
        Assert.Null(result.Kind);
    }

    [Theory]
    [InlineData("/linkidentity")]
    [InlineData("/linkidentity carrier-pigeon")]
    [InlineData("linkidentity 4821")]
    public void Match_TheCommandWordWithNoOrAnInvalidChannelKind_ReturnsInvalidArgument(string body)
    {
        var result = LinkIdentityCommandMatcher.Match(body);

        Assert.Equal(LinkIdentityCommandMatch.InvalidArgument, result.Match);
        Assert.Null(result.Kind);
    }

    /// <summary>`text-commands.md`'s own matching rule: the first token only, never a substring - a
    /// visitor whose message merely contains the word somewhere else gets no special treatment.</summary>
    [Fact]
    public void Match_TheWordMidSentence_ReturnsNotACommand()
    {
        var result = LinkIdentityCommandMatcher.Match("someone told me to type linkidentity telegram");

        Assert.Equal(LinkIdentityCommandMatch.NotACommand, result.Match);
    }
}

public class ReservedChatCommandsTests
{
    [Theory]
    [InlineData("linkidentity")]
    [InlineData("LinkIdentity")]
    [InlineData("/linkidentity")]
    [InlineData("/LINKIDENTITY")]
    public void IsReserved_TheReservedWordInAnyCaseOrSlashForm_ReturnsTrue(string word) =>
        Assert.True(ReservedChatCommands.IsReserved(word));

    [Theory]
    [InlineData("booking")]
    [InlineData("linkidentityx")]
    [InlineData("link")]
    public void IsReserved_AnOrdinaryWord_ReturnsFalse(string word) =>
        Assert.False(ReservedChatCommands.IsReserved(word));
}
