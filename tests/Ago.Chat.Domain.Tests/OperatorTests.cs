namespace Ago.Chat.Domain.Tests;

public class OperatorTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_WhenCapacityIsNotPositive_Throws(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Operator(new OperatorId(Guid.NewGuid()), new SiteId(Guid.NewGuid()), OperatorStatus.Offline, capacity));
    }

    [Fact]
    public void Constructor_WhenValid_SetsProperties()
    {
        var id = new OperatorId(Guid.NewGuid());
        var siteId = new SiteId(Guid.NewGuid());

        var op = new Operator(id, siteId, OperatorStatus.Online, capacity: 5);

        Assert.Equal(id, op.Id);
        Assert.Equal(siteId, op.SiteId);
        Assert.Equal(OperatorStatus.Online, op.Status);
        Assert.Equal(5, op.Capacity);
        Assert.Null(op.ExternalSubjectId);
    }

    [Fact]
    public void Constructor_WithAnExternalSubjectId_SetsIt()
    {
        var op = new Operator(
            new OperatorId(Guid.NewGuid()), new SiteId(Guid.NewGuid()), OperatorStatus.Online, capacity: 5,
            externalSubjectId: "keycloak-sub-123");

        Assert.Equal("keycloak-sub-123", op.ExternalSubjectId);
    }

    // `4-06`: GoOnline/GoOffline exist because nothing did before this fix - see Operator's own type
    // and method remarks for the live bug this closes.
    [Fact]
    public void GoOnline_WhenOffline_BecomesOnline()
    {
        var op = new Operator(new OperatorId(Guid.NewGuid()), new SiteId(Guid.NewGuid()), OperatorStatus.Offline, capacity: 5);

        op.GoOnline();

        Assert.Equal(OperatorStatus.Online, op.Status);
    }

    [Fact]
    public void GoOnline_WhenAlreadyOnline_IsANoOp()
    {
        var op = new Operator(new OperatorId(Guid.NewGuid()), new SiteId(Guid.NewGuid()), OperatorStatus.Online, capacity: 5);

        op.GoOnline();

        Assert.Equal(OperatorStatus.Online, op.Status);
    }

    [Fact]
    public void GoOffline_WhenOnline_BecomesOffline()
    {
        var op = new Operator(new OperatorId(Guid.NewGuid()), new SiteId(Guid.NewGuid()), OperatorStatus.Online, capacity: 5);

        op.GoOffline();

        Assert.Equal(OperatorStatus.Offline, op.Status);
    }
}
