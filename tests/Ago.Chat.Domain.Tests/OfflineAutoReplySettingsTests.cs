namespace Ago.Chat.Domain.Tests;

/// <summary>`14-04`: the scripted matcher, tested where it lives - a pure function over text and
/// configuration, with no clock, no database and nothing to fake (testing.md's domain-unit level).</summary>
public class OfflineAutoReplySettingsTests
{
    [Fact]
    public void Match_WhenAKeywordAppears_ReturnsThatRulesReply()
    {
        var settings = new OfflineAutoReplySettings(
            enabled: true,
            fallbackReply: "We are closed right now.",
            rules: [new OfflineAutoReplyRule("refund", "Refunds take three working days.")]);

        Assert.Equal("Refunds take three working days.", settings.Match("can i get a refund please"));
    }

    [Fact]
    public void Match_IsCaseInsensitive()
    {
        var settings = new OfflineAutoReplySettings(
            enabled: true, fallbackReply: "Closed.", rules: [new OfflineAutoReplyRule("Refund", "Three days.")]);

        Assert.Equal("Three days.", settings.Match("REFUND?"));
    }

    [Fact]
    public void Match_WhenSeveralKeywordsAppear_ReturnsTheFirstRuleInOrder()
    {
        var settings = new OfflineAutoReplySettings(
            enabled: true,
            fallbackReply: "Closed.",
            rules:
            [
                new OfflineAutoReplyRule("delivery", "Delivery is two days."),
                new OfflineAutoReplyRule("refund", "Refunds take three days."),
            ]);

        // Both keywords are present; order in the list decides, not position in the text and not
        // keyword length - see OfflineAutoReplySettings.Match's own remarks.
        Assert.Equal("Delivery is two days.", settings.Match("a refund on my delivery"));
    }

    [Fact]
    public void Match_WhenNoKeywordAppears_FallsBackToTheFallbackReply()
    {
        var settings = new OfflineAutoReplySettings(
            enabled: true, fallbackReply: "We are closed.", rules: [new OfflineAutoReplyRule("refund", "Three days.")]);

        Assert.Equal("We are closed.", settings.Match("hello?"));
    }

    [Fact]
    public void Match_WithNothingConfiguredToSay_ReturnsNull()
    {
        var settings = new OfflineAutoReplySettings(enabled: false, fallbackReply: "", rules: []);

        Assert.Null(settings.Match("hello?"));
    }

    [Fact]
    public void Disabled_IsOffWithNothingToSay()
    {
        Assert.False(OfflineAutoReplySettings.Disabled.Enabled);
        Assert.Empty(OfflineAutoReplySettings.Disabled.Rules);
        Assert.Null(OfflineAutoReplySettings.Disabled.Match("anything"));
    }

    [Fact]
    public void Constructor_WhenEnabledWithNoFallback_Throws()
    {
        // "Enabled with nothing to say" is a setting that looks on and does nothing - refused at the
        // value object, translated to a client error by UpdateOfflineAutoReplyHandler.
        Assert.Throws<ArgumentException>(() =>
            new OfflineAutoReplySettings(enabled: true, fallbackReply: "   ", rules: []));
    }

    [Fact]
    public void Constructor_WhenDisabledWithNoFallback_IsAllowed()
    {
        var settings = new OfflineAutoReplySettings(enabled: false, fallbackReply: "", rules: []);

        Assert.False(settings.Enabled);
    }

    [Fact]
    public void Constructor_WithMoreRulesThanTheCap_Throws()
    {
        var tooMany = Enumerable
            .Range(0, OfflineAutoReplySettings.MaxRules + 1)
            .Select(i => new OfflineAutoReplyRule($"k{i}", "reply"))
            .ToList();

        Assert.Throws<ArgumentException>(() =>
            new OfflineAutoReplySettings(enabled: true, fallbackReply: "Closed.", rules: tooMany));
    }

    [Theory]
    [InlineData("", "reply")]
    [InlineData("   ", "reply")]
    [InlineData("keyword", "")]
    [InlineData("keyword", "   ")]
    public void Rule_WithAnEmptyField_Throws(string keyword, string reply) =>
        Assert.Throws<ArgumentException>(() => new OfflineAutoReplyRule(keyword, reply));

    [Fact]
    public void Rule_TrimsItsKeyword()
    {
        var rule = new OfflineAutoReplyRule("  refund  ", "Three days.");

        Assert.Equal("refund", rule.Keyword);
        Assert.True(rule.Matches("REFUND"));
    }

    [Fact]
    public void Rule_WithAnOversizedReply_Throws() =>
        Assert.Throws<ArgumentException>(() =>
            new OfflineAutoReplyRule("refund", new string('x', OfflineAutoReplyRule.MaxReplyLength + 1)));
}
