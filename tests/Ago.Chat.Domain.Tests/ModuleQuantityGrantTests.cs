namespace Ago.Chat.Domain.Tests;

/// <summary>`22-07`: "site X's module K has quantity Q" - the calendar add-on's own "N masters" is
/// the first real instance. See <see cref="ModuleQuantityGrant"/>'s own remarks for why it is a
/// snapshot rather than a delta.</summary>
public class ModuleQuantityGrantTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Grant_SetsTheQuantity()
    {
        var grant = ModuleQuantityGrant.Grant(SiteId, new ModuleKey("calendar"), 5, Now);

        Assert.Equal(5, grant.Quantity);
        Assert.Equal(Now, grant.GrantedAt);
        Assert.Equal(SiteId, grant.SiteId);
        Assert.Equal(new ModuleKey("calendar"), grant.ModuleKey);
    }

    [Fact]
    public void Grant_RejectsANegativeQuantity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ModuleQuantityGrant.Grant(SiteId, new ModuleKey("calendar"), -1, Now));
    }

    [Fact]
    public void SetQuantity_ReplacesTheNumberAndTheTimestamp()
    {
        var grant = ModuleQuantityGrant.Grant(SiteId, new ModuleKey("calendar"), 5, Now);

        grant.SetQuantity(2, Now.AddDays(1));

        Assert.Equal(2, grant.Quantity);
        Assert.Equal(Now.AddDays(1), grant.GrantedAt);
    }

    [Fact]
    public void SetQuantity_IsASnapshotNotADelta_ReapplyingTheSameValueIsANoOp()
    {
        var grant = ModuleQuantityGrant.Grant(SiteId, new ModuleKey("calendar"), 5, Now);

        grant.SetQuantity(5, Now.AddSeconds(1));

        Assert.Equal(5, grant.Quantity);
    }

    [Fact]
    public void SetQuantity_RejectsANegativeQuantity()
    {
        var grant = ModuleQuantityGrant.Grant(SiteId, new ModuleKey("calendar"), 5, Now);

        Assert.Throws<ArgumentOutOfRangeException>(() => grant.SetQuantity(-1, Now));
    }
}
