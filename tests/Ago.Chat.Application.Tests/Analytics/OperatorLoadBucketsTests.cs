using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.Tests.Analytics;

/// <summary>`23-17`: a pure-function proof, no database at all - <see cref="OperatorLoadBuckets"/>'s own
/// remarks on why that placement is correct and this test is cheap.</summary>
public class OperatorLoadBucketsTests
{
    private static readonly int[] DefaultBounds = [1, 3, 5, 8];

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    [InlineData(5, 2)]
    [InlineData(6, 3)]
    [InlineData(8, 3)]
    [InlineData(9, 4)]
    [InlineData(100, 4)]
    public void IndexOf_AgainstTheDefaultBounds_MatchesHandComputedBucketing(int concurrentLoad, int expectedIndex)
    {
        Assert.Equal(expectedIndex, OperatorLoadBuckets.IndexOf(DefaultBounds, concurrentLoad));
    }

    [Theory]
    [InlineData(0, "1")]
    [InlineData(1, "2-3")]
    [InlineData(2, "4-5")]
    [InlineData(3, "6-8")]
    [InlineData(4, "9+")]
    public void Label_AgainstTheDefaultBounds_MatchesHandComputedLabels(int index, string expectedLabel)
    {
        Assert.Equal(expectedLabel, OperatorLoadBuckets.Label(DefaultBounds, index));
    }

    /// <summary>A single-bound configuration collapses to two buckets: "at or under the bound" and
    /// "over it" - the smallest configuration <see cref="OperatorLoadBuckets.IsValidConfiguration"/>
    /// still accepts.</summary>
    [Fact]
    public void IndexOfAndLabel_WithASingleBound_ProduceExactlyTwoBuckets()
    {
        int[] bounds = [5];

        Assert.Equal(0, OperatorLoadBuckets.IndexOf(bounds, 3));
        Assert.Equal(0, OperatorLoadBuckets.IndexOf(bounds, 5));
        Assert.Equal(1, OperatorLoadBuckets.IndexOf(bounds, 6));
        Assert.Equal("1-5", OperatorLoadBuckets.Label(bounds, 0));
        Assert.Equal("6+", OperatorLoadBuckets.Label(bounds, 1));
    }

    [Theory]
    [InlineData(new int[] { }, false)] // empty
    [InlineData(new[] { 0 }, false)] // not positive
    [InlineData(new[] { -1, 3 }, false)] // not positive
    [InlineData(new[] { 3, 3 }, false)] // not strictly ascending
    [InlineData(new[] { 5, 3 }, false)] // descending
    [InlineData(new[] { 1, 3, 5 }, true)]
    [InlineData(new[] { 1 }, true)]
    public void IsValidConfiguration_RejectsAnythingNotAscendingPositiveAndNonEmpty(int[] bounds, bool expected)
    {
        Assert.Equal(expected, OperatorLoadBuckets.IsValidConfiguration(bounds));
    }
}
