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

    // `13-03`: every operator created today is created within `13-01`'s own seat-limit check and
    // therefore already fits - HoldsSeat defaults true, RemovedAt defaults null.
    [Fact]
    public void Constructor_WhenValid_DefaultsHoldsSeatTrueAndRemovedAtNull()
    {
        var op = new Operator(new OperatorId(Guid.NewGuid()), new SiteId(Guid.NewGuid()), OperatorStatus.Offline, capacity: 5);

        Assert.True(op.HoldsSeat);
        Assert.Null(op.RemovedAt);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ToggleSeat_SetsHoldsSeat(bool holdsSeat)
    {
        var op = new Operator(new OperatorId(Guid.NewGuid()), new SiteId(Guid.NewGuid()), OperatorStatus.Offline, capacity: 5);

        op.ToggleSeat(holdsSeat);

        Assert.Equal(holdsSeat, op.HoldsSeat);
    }

    [Fact]
    public void Remove_WhenNotAlreadyRemoved_StampsRemovedAtAndRaisesOperatorRemoved()
    {
        var id = new OperatorId(Guid.NewGuid());
        var siteId = new SiteId(Guid.NewGuid());
        var op = new Operator(id, siteId, OperatorStatus.Offline, capacity: 5);
        var now = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

        op.Remove(now);

        Assert.Equal(now, op.RemovedAt);
        var raised = Assert.Single(op.DomainEvents.OfType<OperatorRemoved>());
        Assert.Equal(id, raised.OperatorId);
        Assert.Equal(siteId, raised.SiteId);
        Assert.Equal(now, raised.OccurredAt);
    }

    [Fact]
    public void Remove_WhenAlreadyRemoved_Throws()
    {
        var op = new Operator(new OperatorId(Guid.NewGuid()), new SiteId(Guid.NewGuid()), OperatorStatus.Offline, capacity: 5);
        op.Remove(new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero));

        Assert.Throws<InvalidOperationException>(() => op.Remove(new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void ClearDomainEvents_RemovesEverythingRaised()
    {
        var op = new Operator(new OperatorId(Guid.NewGuid()), new SiteId(Guid.NewGuid()), OperatorStatus.Offline, capacity: 5);
        op.Remove(new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero));

        op.ClearDomainEvents();

        Assert.Empty(op.DomainEvents);
    }
}
