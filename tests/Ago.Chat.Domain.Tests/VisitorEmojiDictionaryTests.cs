namespace Ago.Chat.Domain.Tests;

/// <summary>`25-56` decision 3: "each with enough neutral, visually-distinct members" - these tests
/// pin the two shape properties that claim actually depends on: every member is present exactly once
/// (a duplicate would silently shrink the real combination count below what decision 3 asks for), and
/// the two lists never share a member (a Creatures/Foods overlap would make "two emoji, from two
/// different groups" - decision 2 - stop being true for that member).</summary>
public class VisitorEmojiDictionaryTests
{
    [Fact]
    public void Creatures_HasNoDuplicates() =>
        Assert.Equal(VisitorEmojiDictionary.Creatures.Count, VisitorEmojiDictionary.Creatures.Distinct().Count());

    [Fact]
    public void Foods_HasNoDuplicates() =>
        Assert.Equal(VisitorEmojiDictionary.Foods.Count, VisitorEmojiDictionary.Foods.Distinct().Count());

    [Fact]
    public void CreaturesAndFoods_ShareNoMember() =>
        Assert.Empty(VisitorEmojiDictionary.Creatures.Intersect(VisitorEmojiDictionary.Foods));

    /// <summary>Decision 3's own range - "15-30" - the brief's stated floor and ceiling.</summary>
    [Theory]
    [InlineData(15, 30)]
    public void BothLists_FallWithinTheStatedRange(int min, int max)
    {
        Assert.InRange(VisitorEmojiDictionary.Creatures.Count, min, max);
        Assert.InRange(VisitorEmojiDictionary.Foods.Count, min, max);
    }
}
