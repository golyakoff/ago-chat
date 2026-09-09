namespace Ago.Chat.Domain.Tests;

/// <summary>`23-88`: "site X asked how many of module K's own things candidate quantity Q would
/// exceed" - see <see cref="ModuleQuantityImpactPreview"/>'s own remarks for why this is a snapshot
/// per (site, module), the identical shape <see cref="ModuleQuantityGrant"/>'s own tests already
/// establish for its sibling.</summary>
public class ModuleQuantityImpactPreviewTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly ModuleKey ModuleKey = new("calendar");
    private static readonly DateTimeOffset Now = new(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Request_SetsTheCandidateQuantity_AndLeavesTheAnswerUnset()
    {
        var preview = ModuleQuantityImpactPreview.Request(SiteId, ModuleKey, 2, Now);

        Assert.Equal(SiteId, preview.SiteId);
        Assert.Equal(ModuleKey, preview.ModuleKey);
        Assert.Equal(2, preview.RequestedQuantity);
        Assert.Equal(Now, preview.RequestedAt);
        Assert.Null(preview.AffectedCount);
        Assert.Empty(preview.AffectedItemDisplayNames);
        Assert.Null(preview.AnsweredAt);
    }

    [Fact]
    public void Request_RejectsANegativeQuantity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ModuleQuantityImpactPreview.Request(SiteId, ModuleKey, -1, Now));
    }

    [Fact]
    public void Answer_RecordsTheCountNamesAndTimestamp()
    {
        var preview = ModuleQuantityImpactPreview.Request(SiteId, ModuleKey, 2, Now);

        preview.Answer(3, new[] { "Anna", "Boris", "Vera" }, Now.AddSeconds(4));

        Assert.Equal(3, preview.AffectedCount);
        Assert.Equal(new[] { "Anna", "Boris", "Vera" }, preview.AffectedItemDisplayNames);
        Assert.Equal(Now.AddSeconds(4), preview.AnsweredAt);
        // The question itself is untouched by answering it.
        Assert.Equal(2, preview.RequestedQuantity);
    }

    [Fact]
    public void Answer_RejectsANegativeAffectedCount()
    {
        var preview = ModuleQuantityImpactPreview.Request(SiteId, ModuleKey, 2, Now);

        Assert.Throws<ArgumentOutOfRangeException>(() => preview.Answer(-1, [], Now));
    }

    /// <summary>`23-88`'s own "a fresh question supersedes whatever this row held before" rule -
    /// asking again about a different candidate clears a prior answer rather than leaving it sitting
    /// beside a number it no longer describes.</summary>
    [Fact]
    public void Reset_ClearsAPriorAnswer_AndSetsTheNewCandidateQuantity()
    {
        var preview = ModuleQuantityImpactPreview.Request(SiteId, ModuleKey, 2, Now);
        preview.Answer(3, new[] { "Anna", "Boris", "Vera" }, Now.AddSeconds(4));

        preview.Reset(5, Now.AddMinutes(1));

        Assert.Equal(5, preview.RequestedQuantity);
        Assert.Equal(Now.AddMinutes(1), preview.RequestedAt);
        Assert.Null(preview.AffectedCount);
        Assert.Empty(preview.AffectedItemDisplayNames);
        Assert.Null(preview.AnsweredAt);
    }

    [Fact]
    public void Reset_RejectsANegativeQuantity()
    {
        var preview = ModuleQuantityImpactPreview.Request(SiteId, ModuleKey, 2, Now);

        Assert.Throws<ArgumentOutOfRangeException>(() => preview.Reset(-1, Now));
    }
}
