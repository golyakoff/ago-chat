namespace Ago.Chat.Domain.Tests;

/// <summary>`25-43`: a pure aggregate with no clock, no database and nothing to fake (testing.md's
/// domain-unit level), the same shape <see cref="DocumentTests"/> already establishes for the
/// identical grandfathering shape - deliberately mirrored, restated for a price instead of a
/// document. Covers the item's own Done-when invariants at the level with the fewest moving parts: a
/// superseded price stays readable at its own version after a new one publishes, and a version's own
/// amount, once minted, never changes underneath a caller that already read it.</summary>
public class PricedResourceTests
{
    private static readonly PricedResourceId Id = new(Guid.NewGuid());
    private static readonly PriceKey Key = new("seat-base");
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_StartsWithNoVersions()
    {
        var resource = PricedResource.Create(Id, Key);

        Assert.Equal(Key, resource.Key);
        Assert.Equal(0, resource.LastSequence);
        Assert.Empty(resource.Versions);
        Assert.Null(resource.Current);
    }

    [Fact]
    public void Publish_MintsTheFirstVersionAsV1()
    {
        var resource = PricedResource.Create(Id, Key);

        var version = resource.Publish(new PublishedPriceVersionId(Guid.NewGuid()), 490m, Now);

        Assert.Equal(1, version.Sequence);
        Assert.Equal("v1", version.Version);
        Assert.Equal(1, resource.LastSequence);
        Assert.Same(version, resource.Current);
        Assert.Single(resource.Versions);
        Assert.Equal(490m, version.AmountRub);
    }

    // `25-43`'s own Done-when, proven here at the aggregate level before any repository or HTTP layer
    // is involved: "a completed charge can be read back showing the exact price version it was
    // charged under" only holds if publishing a new version never disturbs the old one - this is that
    // guarantee's own root cause.
    [Fact]
    public void Publish_ASecondTime_MintsANewVersionAndKeepsTheFirstReadableAtItsOwnUnchangedAmount()
    {
        var resource = PricedResource.Create(Id, Key);
        var v1 = resource.Publish(new PublishedPriceVersionId(Guid.NewGuid()), 490m, Now);

        var v2 = resource.Publish(new PublishedPriceVersionId(Guid.NewGuid()), 550m, Now.AddMonths(1));

        Assert.Equal(2, resource.Versions.Count);
        Assert.Contains(v1, resource.Versions);
        Assert.Contains(v2, resource.Versions);
        Assert.Equal("v1", v1.Version);
        Assert.Equal("v2", v2.Version);
        Assert.Same(v2, resource.Current);
        // The historical figure a v1-priced charge was actually charged under is exactly what v1
        // still reports - publishing v2 never touches it.
        Assert.Equal(490m, v1.AmountRub);
        Assert.Equal(550m, v2.AmountRub);
    }

    [Fact]
    public void Publish_EachVersionCarriesTheParentsKeyAndId()
    {
        var resource = PricedResource.Create(Id, Key);

        var version = resource.Publish(new PublishedPriceVersionId(Guid.NewGuid()), 490m, Now);

        Assert.Equal(Key, version.Key);
        Assert.Equal(resource.Id, version.PricedResourceId);
    }

    [Fact]
    public void Publish_WithANegativeAmount_Throws()
    {
        var resource = PricedResource.Create(Id, Key);
        Assert.Throws<ArgumentOutOfRangeException>(() => resource.Publish(new PublishedPriceVersionId(Guid.NewGuid()), -1m, Now));
    }

    [Fact]
    public void Publish_WithAZeroAmount_Succeeds()
    {
        // `25-43`'s own second decision, restated at the aggregate: zero is a legal, meaningful price
        // (a promotional rate), never confused with "no price published at all" - that state is
        // Current being null, not a published zero.
        var resource = PricedResource.Create(Id, Key);

        var version = resource.Publish(new PublishedPriceVersionId(Guid.NewGuid()), 0m, Now);

        Assert.Equal(0m, version.AmountRub);
        Assert.NotNull(resource.Current);
    }

    [Fact]
    public void Publish_AFailedAttempt_DoesNotBurnASequenceNumber()
    {
        // Mirrors DocumentTests' own identical assertion for Document.Publish - PricedResource.Publish
        // increments LastSequence before PublishedPriceVersion.Create validates, so a rejected publish
        // (a negative amount) still consumes the number. Documented here on purpose, the same reasoning
        // DocumentTests' own equivalent test gives.
        var resource = PricedResource.Create(Id, Key);

        Assert.Throws<ArgumentOutOfRangeException>(() => resource.Publish(new PublishedPriceVersionId(Guid.NewGuid()), -1m, Now));
        Assert.Equal(1, resource.LastSequence);

        var version = resource.Publish(new PublishedPriceVersionId(Guid.NewGuid()), 100m, Now);
        Assert.Equal("v2", version.Version);
    }
}
