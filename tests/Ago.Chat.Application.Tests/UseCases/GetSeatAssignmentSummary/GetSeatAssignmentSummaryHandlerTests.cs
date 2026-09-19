using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetSeatAssignmentSummary;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetSeatAssignmentSummary;

public class GetSeatAssignmentSummaryHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId RequestedBy = new(Guid.NewGuid());

    private const string OperatorRoleName = "Operator";

    private sealed record Fixture(
        Application.UseCases.GetSeatAssignmentSummary.GetSeatAssignmentSummaryHandler Handler, FakeOperatorRoleRepository OperatorRoles);

    private static Fixture CreateFixture(int seatLimit)
    {
        var operatorRoles = new FakeOperatorRoleRepository();
        var sites = new FakeSiteRepository();
        sites.Seed(new Site(SiteId, $"site_{SiteId.Value:N}", [], tier: SubscriptionTierBands.Starter, seatLimit: seatLimit));

        var permissions = new FakePermissionChecker();
        permissions.Grant(RequestedBy, SiteId, Permission.SiteManageOperators);

        var handler = new Application.UseCases.GetSeatAssignmentSummary.GetSeatAssignmentSummaryHandler(operatorRoles, sites, permissions);
        return new Fixture(handler, operatorRoles);
    }

    [Fact]
    public async Task HandleAsync_WhenHeldSeatsExceedSeatLimit_ReportsOverLimit()
    {
        var fixture = CreateFixture(seatLimit: 1);
        fixture.OperatorRoles.SeedSeat(new OperatorId(Guid.NewGuid()), OperatorRoleName, holdsSeat: true);
        fixture.OperatorRoles.SeedSeat(new OperatorId(Guid.NewGuid()), OperatorRoleName, holdsSeat: true);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetSeatAssignmentSummary.GetSeatAssignmentSummary(RequestedBy, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var operatorRole = result.Value.Roles.Single(r => r.RoleName == OperatorRoleName);
        Assert.Equal(2, operatorRole.HeldSeats);
        Assert.Equal(1, operatorRole.Limit);
        Assert.True(operatorRole.OverLimit);
    }

    [Fact]
    public async Task HandleAsync_WhenHeldSeatsAreWithinLimit_ReportsNotOverLimit()
    {
        var fixture = CreateFixture(seatLimit: 3);
        fixture.OperatorRoles.SeedSeat(new OperatorId(Guid.NewGuid()), OperatorRoleName, holdsSeat: true);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetSeatAssignmentSummary.GetSeatAssignmentSummary(RequestedBy, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var operatorRole = result.Value.Roles.Single(r => r.RoleName == OperatorRoleName);
        Assert.Equal(1, operatorRole.HeldSeats);
        Assert.False(operatorRole.OverLimit);
    }

    [Fact]
    public async Task HandleAsync_ExcludesRemovedAndSeatlessOperators()
    {
        var fixture = CreateFixture(seatLimit: 5);
        fixture.OperatorRoles.SeedSeat(new OperatorId(Guid.NewGuid()), OperatorRoleName, holdsSeat: true);
        var seatless = new OperatorId(Guid.NewGuid());
        fixture.OperatorRoles.SeedSeat(seatless, OperatorRoleName, holdsSeat: false);
        var removed = new OperatorId(Guid.NewGuid());
        fixture.OperatorRoles.SeedSeat(removed, OperatorRoleName, holdsSeat: true);
        fixture.OperatorRoles.MarkRemoved(removed);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetSeatAssignmentSummary.GetSeatAssignmentSummary(RequestedBy, SiteId), CancellationToken.None);

        var operatorRole = result.Value.Roles.Single(r => r.RoleName == OperatorRoleName);
        Assert.Equal(1, operatorRole.HeldSeats);
    }

    [Fact]
    public async Task HandleAsync_ReturnsBothSeededRoles_OperatorAndAdmin()
    {
        // `25-170`: the console shows the same over-limit banner and toggle for the Admin role that
        // the Operator role already has - this handler's own list must always carry both.
        var fixture = CreateFixture(seatLimit: 5);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetSeatAssignmentSummary.GetSeatAssignmentSummary(RequestedBy, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Contains(result.Value.Roles, r => r.RoleName == "Operator");
        Assert.Contains(result.Value.Roles, r => r.RoleName == "Admin");
    }
}
