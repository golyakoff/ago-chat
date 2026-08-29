using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetSeatAssignmentSummary;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetSeatAssignmentSummary;

public class GetSeatAssignmentSummaryHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId RequestedBy = new(Guid.NewGuid());

    private sealed record Fixture(
        Application.UseCases.GetSeatAssignmentSummary.GetSeatAssignmentSummaryHandler Handler, FakeOperatorRepository Operators);

    private static Fixture CreateFixture(int seatLimit)
    {
        var operators = new FakeOperatorRepository();
        var sites = new FakeSiteRepository();
        sites.Seed(new Site(SiteId, $"site_{SiteId.Value:N}", [], tier: SubscriptionTierBands.Starter, seatLimit: seatLimit));

        var permissions = new FakePermissionChecker();
        permissions.Grant(RequestedBy, SiteId, Permission.SiteManageOperators);

        var handler = new Application.UseCases.GetSeatAssignmentSummary.GetSeatAssignmentSummaryHandler(operators, sites, permissions);
        return new Fixture(handler, operators);
    }

    [Fact]
    public async Task HandleAsync_WhenHeldSeatsExceedSeatLimit_ReportsOverSeats()
    {
        var fixture = CreateFixture(seatLimit: 1);
        fixture.Operators.Seed(new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5));
        fixture.Operators.Seed(new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5));

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetSeatAssignmentSummary.GetSeatAssignmentSummary(RequestedBy, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.HeldSeats);
        Assert.Equal(1, result.Value.SeatLimit);
        Assert.True(result.Value.OverSeats);
    }

    [Fact]
    public async Task HandleAsync_WhenHeldSeatsAreWithinLimit_ReportsNotOverSeats()
    {
        var fixture = CreateFixture(seatLimit: 3);
        fixture.Operators.Seed(new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5));

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetSeatAssignmentSummary.GetSeatAssignmentSummary(RequestedBy, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value.HeldSeats);
        Assert.False(result.Value.OverSeats);
    }

    [Fact]
    public async Task HandleAsync_ExcludesRemovedAndSeatlessOperators()
    {
        var fixture = CreateFixture(seatLimit: 5);
        fixture.Operators.Seed(new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5));
        var seatless = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5, holdsSeat: false);
        fixture.Operators.Seed(seatless);
        var removed = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5);
        removed.Remove(new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero));
        fixture.Operators.Seed(removed);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetSeatAssignmentSummary.GetSeatAssignmentSummary(RequestedBy, SiteId), CancellationToken.None);

        Assert.Equal(1, result.Value.HeldSeats);
    }
}
