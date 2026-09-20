using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.OperatorRoleSeats;
using Ago.Chat.Application.UseCases.ToggleOperatorSeat;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.ToggleOperatorSeat;

public class ToggleOperatorSeatHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId RequestedBy = new(Guid.NewGuid());

    private const string OperatorRoleName = "Operator";

    private sealed record Fixture(
        Application.UseCases.ToggleOperatorSeat.ToggleOperatorSeatHandler Handler,
        FakeOperatorRepository Operators,
        FakeOperatorRoleRepository OperatorRoles,
        FakeSiteRepository Sites);

    private static Fixture CreateFixture(int seatLimit = 3, bool grantPermission = true)
    {
        var operators = new FakeOperatorRepository();
        var operatorRoles = new FakeOperatorRoleRepository();
        var sites = new FakeSiteRepository();
        sites.Seed(new Site(SiteId, $"site_{SiteId.Value:N}", [], tier: SubscriptionTierBands.Starter, seatLimit: seatLimit));

        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(RequestedBy, SiteId, Permission.SiteManageOperators);
        }

        // `25-181`: FakeOwnerSeatGrantStore with nothing seeded - EffectiveExtraAsync reads 0, so
        // every test in this file keeps exercising the identical billing-only limit it always has; the
        // clock value is irrelevant since nothing is seeded for it to check an expiry against.
        var roleSeatCapacity = new OperatorRoleSeatCapacity(
            operatorRoles, sites, new FakeOwnerSeatGrantStore(), new FakeClock(DateTimeOffset.UtcNow));
        var handler = new Application.UseCases.ToggleOperatorSeat.ToggleOperatorSeatHandler(
            operators, operatorRoles, permissions, new FakeUnitOfWork(), roleSeatCapacity);
        return new Fixture(handler, operators, operatorRoles, sites);
    }

    [Fact]
    public async Task HandleAsync_WhenNotPermitted_ReturnsForbidden()
    {
        var fixture = CreateFixture(grantPermission: false);
        var target = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5);
        fixture.Operators.Seed(target);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ToggleOperatorSeat.ToggleOperatorSeat(RequestedBy, SiteId, target.Id, OperatorRoleName, false), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_TogglingOff_NeverBlockedByCapacity()
    {
        var fixture = CreateFixture(seatLimit: 1);
        var target = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5);
        fixture.Operators.Seed(target);
        fixture.OperatorRoles.SeedSeat(target.Id, OperatorRoleName, holdsSeat: true);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ToggleOperatorSeat.ToggleOperatorSeat(RequestedBy, SiteId, target.Id, OperatorRoleName, false), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(await fixture.OperatorRoles.HoldsRoleSeatAsync(target.Id, SiteId, OperatorRoleName, CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_TogglingOn_WhenAtCapacity_ReturnsSeatLimitReached()
    {
        var fixture = CreateFixture(seatLimit: 1);
        var alreadyHolding = new OperatorId(Guid.NewGuid());
        fixture.OperatorRoles.SeedSeat(alreadyHolding, OperatorRoleName, holdsSeat: true);
        var target = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5);
        fixture.Operators.Seed(target);
        fixture.OperatorRoles.SeedSeat(target.Id, OperatorRoleName, holdsSeat: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ToggleOperatorSeat.ToggleOperatorSeat(RequestedBy, SiteId, target.Id, OperatorRoleName, true), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Operator.SeatLimitReached", result.Error!.Value.Code);
        Assert.False(await fixture.OperatorRoles.HoldsRoleSeatAsync(target.Id, SiteId, OperatorRoleName, CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_TogglingOn_WhenUnderCapacity_Succeeds()
    {
        var fixture = CreateFixture(seatLimit: 3);
        var target = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5);
        fixture.Operators.Seed(target);
        fixture.OperatorRoles.SeedSeat(target.Id, OperatorRoleName, holdsSeat: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ToggleOperatorSeat.ToggleOperatorSeat(RequestedBy, SiteId, target.Id, OperatorRoleName, true), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(await fixture.OperatorRoles.HoldsRoleSeatAsync(target.Id, SiteId, OperatorRoleName, CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_WhenTargetNotFoundForThisSite_ReturnsNotFound()
    {
        var fixture = CreateFixture();
        var otherSiteId = new SiteId(Guid.NewGuid());
        var target = new Operator(new OperatorId(Guid.NewGuid()), otherSiteId, OperatorStatus.Offline, capacity: 5);
        fixture.Operators.Seed(target);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ToggleOperatorSeat.ToggleOperatorSeat(RequestedBy, SiteId, target.Id, OperatorRoleName, true), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Operator.NotFound", result.Error!.Value.Code);
    }

    /// <summary>`25-170`'s own generalisation - the Admin role gets the identical capacity guard the
    /// Operator role already had, through the same handler and the same unified capacity-check
    /// procedure.</summary>
    [Fact]
    public async Task HandleAsync_TogglingOnTheAdminRole_WhenAtCapacity_ReturnsAdminLimitReached()
    {
        // The Starter tier's own AdminLimit is 2 (SubscriptionTierBands.BusinessAdminsIncluded) - two
        // existing Admin-role seats already fill it.
        var fixture = CreateFixture();
        fixture.OperatorRoles.SeedSeat(new OperatorId(Guid.NewGuid()), "Admin", holdsSeat: true);
        fixture.OperatorRoles.SeedSeat(new OperatorId(Guid.NewGuid()), "Admin", holdsSeat: true);
        var target = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5);
        fixture.Operators.Seed(target);
        fixture.OperatorRoles.SeedSeat(target.Id, "Admin", holdsSeat: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ToggleOperatorSeat.ToggleOperatorSeat(RequestedBy, SiteId, target.Id, "Admin", true), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Operator.AdminLimitReached", result.Error!.Value.Code);
    }
}
