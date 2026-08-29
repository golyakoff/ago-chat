namespace Ago.Chat.Domain.Tests;

/// <summary>`20-07`: `adr/0065` decision 6 ("no intent detection in v1") given a concrete boundary -
/// see <see cref="TriggerCommandMatcher"/>'s own remarks.</summary>
public class TriggerCommandMatcherTests
{
    private static readonly ModuleKey Calendar = new("calendar");
    private static readonly ModuleKey Taxi = new("taxi");

    [Fact]
    public void Match_WhenTheFirstTokenEqualsATriggerWord_ReturnsThatModule()
    {
        var candidates = new[]
        {
            new TriggerCommandMatcher.Candidate(Calendar, ["/booking", "book"]),
        };

        var result = TriggerCommandMatcher.Match("/booking", candidates);

        Assert.Equal(Calendar, result);
    }

    [Fact]
    public void Match_IsCaseInsensitive()
    {
        var candidates = new[] { new TriggerCommandMatcher.Candidate(Calendar, ["book"]) };

        Assert.Equal(Calendar, TriggerCommandMatcher.Match("BOOK", candidates));
        Assert.Equal(Calendar, TriggerCommandMatcher.Match("Book please", candidates));
    }

    /// <summary>The item's own explicit example: `adr/0065` decision 6 says a visitor typing "I'd like
    /// to book" gets no special treatment. Only the first token is ever compared.</summary>
    [Fact]
    public void Match_DoesNotMatchATriggerWordAppearingMidSentence()
    {
        var candidates = new[] { new TriggerCommandMatcher.Candidate(Calendar, ["book"]) };

        var result = TriggerCommandMatcher.Match("I'd like to book a table please", candidates);

        Assert.Null(result);
    }

    [Fact]
    public void Match_WithNoCandidateMatching_ReturnsNull()
    {
        var candidates = new[] { new TriggerCommandMatcher.Candidate(Calendar, ["book"]) };

        Assert.Null(TriggerCommandMatcher.Match("hello there", candidates));
    }

    [Fact]
    public void Match_WithMultipleCandidates_MatchesTheRightOne()
    {
        var candidates = new[]
        {
            new TriggerCommandMatcher.Candidate(Calendar, ["book"]),
            new TriggerCommandMatcher.Candidate(Taxi, ["ride"]),
        };

        Assert.Equal(Taxi, TriggerCommandMatcher.Match("ride please", candidates));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Match_WithEmptyBody_ReturnsNull(string body)
    {
        var candidates = new[] { new TriggerCommandMatcher.Candidate(Calendar, ["book"]) };

        Assert.Null(TriggerCommandMatcher.Match(body, candidates));
    }

    [Fact]
    public void Match_WithNoCandidates_ReturnsNull() =>
        Assert.Null(TriggerCommandMatcher.Match("book", []));

    /// <summary>A trigger word registered with a leading slash still matches a message that omits it,
    /// and vice versa - the match is about the command name, not the punctuation convention.</summary>
    [Theory]
    [InlineData("/book", "book")]
    [InlineData("book", "/book")]
    public void Match_IgnoresALeadingSlashOnEitherSide(string triggerWord, string messageBody)
    {
        var candidates = new[] { new TriggerCommandMatcher.Candidate(Calendar, [triggerWord]) };

        Assert.Equal(Calendar, TriggerCommandMatcher.Match(messageBody, candidates));
    }
}
