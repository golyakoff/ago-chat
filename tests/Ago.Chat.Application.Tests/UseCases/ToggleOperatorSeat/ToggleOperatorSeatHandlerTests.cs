using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.ToggleOperatorSeat;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.ToggleOperatorSeat;

public class ToggleOperatorSeatHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId RequestedBy = new(Guid.NewGuid());

    private sealed record Fixture(
        Application.UseCases.ToggleOperatorSeat.ToggleOperatorSeatHandler Handler, FakeOperatorRepository Operators, FakeSiteRepository Sites);

    private static Fixture CreateFixture(int seatLimit = 3, bool grantPermission = true)
    {
        var operators = new FakeOperatorRepository();
        var sites = new FakeSiteRepository();
        sites.Seed(new Site(SiteId, $"site_{SiteId.Value:N}", [], tier: SubscriptionTierBands.Starter, seatLimit: seatLimit));

        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(RequestedBy, SiteId, Permission.SiteManageOperators);
        }

        var handler = new Application.UseCases.ToggleOperatorSeat.ToggleOperatorSeatHandler(operators, sites, permissions);
        return new Fixture(handler, operators, sites);
    }

    [Fact]
    public async Task HandleAsync_WhenNotPermitted_ReturnsForbidden()
    {
        var fixture = CreateFixture(grantPermission: false);
        var target = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5);
        fixture.Operators.Seed(target);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ToggleOperatorSeat.ToggleOperatorSeat(RequestedBy, SiteId, target.Id, false), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_TogglingOff_NeverBlockedByCapacity()
    {
        var fixture = CreateFixture(seatLimit: 1);
        var target = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5);
        fixture.Operators.Seed(target);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ToggleOperatorSeat.ToggleOperatorSeat(RequestedBy, SiteId, target.Id, false), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(target.HoldsSeat);
    }

    [Fact]
    public async Task HandleAsync_TogglingOn_WhenAtCapacity_ReturnsSeatLimitReached()
    {
        var fixture = CreateFixture(seatLimit: 1);
        var alreadyHolding = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5);
        fixture.Operators.Seed(alreadyHolding);
        var target = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5, holdsSeat: false);
        fixture.Operators.Seed(target);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ToggleOperatorSeat.ToggleOperatorSeat(RequestedBy, SiteId, target.Id, true), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Operator.SeatLimitReached", result.Error!.Value.Code);
        Assert.False(target.HoldsSeat);
    }

    [Fact]
    public async Task HandleAsync_TogglingOn_WhenUnderCapacity_Succeeds()
    {
        var fixture = CreateFixture(seatLimit: 3);
        var target = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5, holdsSeat: false);
        fixture.Operators.Seed(target);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ToggleOperatorSeat.ToggleOperatorSeat(RequestedBy, SiteId, target.Id, true), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(target.HoldsSeat);
    }

    [Fact]
    public async Task HandleAsync_WhenTargetNotFoundForThisSite_ReturnsNotFound()
    {
        var fixture = CreateFixture();
        var otherSiteId = new SiteId(Guid.NewGuid());
        var target = new Operator(new OperatorId(Guid.NewGuid()), otherSiteId, OperatorStatus.Offline, capacity: 5);
        fixture.Operators.Seed(target);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ToggleOperatorSeat.ToggleOperatorSeat(RequestedBy, SiteId, target.Id, true), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Operator.NotFound", result.Error!.Value.Code);
    }
}
