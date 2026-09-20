using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.OperatorRoleSeats;
using Ago.Chat.Application.UseCases.ResolveOperatorIdentity;
using Ago.Chat.Application.UseCases.RestoreOperatorSeatAsOwner;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.RestoreOperatorSeatAsOwner;

/// <summary>`23-68`'s own Done-when at the Application level: "a platform owner can restore a
/// locked-out operator's ability to sign in" - proven here via
/// <see cref="OperatorSignInEligibility.CanSignInAsync"/>, the exact function that decides whether an
/// operator may sign in at all (`ResolveOperatorIdentityHandler`'s own caller). "The seat-limit
/// interaction is decided... rather than discovered" is proven by the Force/Reason override tests below.
///
/// <para><b>`25-170`: this handler stays scoped to the seeded Operator role</b> - see the handler's own
/// class-level remarks for why; every test here seeds and asserts the Operator role's own seat
/// specifically.</para>
/// </summary>
public class RestoreOperatorSeatAsOwnerHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private const string PlatformOwnerSubject = "keycloak-sub-of-the-platform-owner";
    private const string ValidReason = "Tenant locked itself out during a live demo; restoring the sole operator's seat.";
    private const string OperatorRoleName = "Operator";

    private sealed record Fixture(
        Application.UseCases.RestoreOperatorSeatAsOwner.RestoreOperatorSeatAsOwnerHandler Handler,
        FakeOperatorRepository Operators, FakeOperatorRoleRepository OperatorRoles, FakeSiteRepository Sites,
        FakeOperatorSeatRestoreOverrideRepository Overrides);

    private static Fixture CreateFixture(int seatLimit = 3)
    {
        var operators = new FakeOperatorRepository();
        var operatorRoles = new FakeOperatorRoleRepository();
        var sites = new FakeSiteRepository();
        sites.Seed(new Site(SiteId, $"site_{SiteId.Value:N}", [], tier: SubscriptionTierBands.Starter, seatLimit: seatLimit));
        var overrides = new FakeOperatorSeatRestoreOverrideRepository();
        // `25-181`: FakeOwnerSeatGrantStore with nothing seeded - EffectiveExtraAsync reads 0, so
        // every test in this file keeps exercising the identical billing-only limit it always has.
        var roleSeatCapacity = new OperatorRoleSeatCapacity(operatorRoles, sites, new FakeOwnerSeatGrantStore(), new FakeClock(Now));

        var handler = new Application.UseCases.RestoreOperatorSeatAsOwner.RestoreOperatorSeatAsOwnerHandler(
            operators, operatorRoles, new FakeUnitOfWork(), roleSeatCapacity, overrides, new FakeClock(Now), new FakeIdGenerator());
        return new Fixture(handler, operators, operatorRoles, sites, overrides);
    }

    private static Application.UseCases.RestoreOperatorSeatAsOwner.RestoreOperatorSeatAsOwner Command(
        OperatorId targetOperatorId, bool force = false, string? reason = null) =>
        new(SiteId, targetOperatorId, PlatformOwnerSubject, force, reason);

    /// <summary>The item's own headline claim, proven at the domain level rather than asserted: an
    /// operator who holds no seat on any role - the exact shape `25-170`'s one-rule `CanSignIn` refuses -
    /// cannot sign in before this call, and can afterward. This is "locked out" and "let back in" made
    /// concrete, not merely a seat flag flipping.</summary>
    [Fact]
    public async Task HandleAsync_ForALockedOutOperator_MakesCanSignInAsync_TrueAfterwards_WhereItWasFalseBefore()
    {
        var fixture = CreateFixture();
        var target = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5);
        fixture.Operators.Seed(target);
        fixture.OperatorRoles.SeedSeat(target.Id, OperatorRoleName, holdsSeat: false);

        var before = await OperatorSignInEligibility.CanSignInAsync(target, fixture.OperatorRoles, CancellationToken.None);
        Assert.False(before);

        var result = await fixture.Handler.HandleAsync(Command(target.Id), CancellationToken.None);
        Assert.True(result.IsSuccess);

        var after = await OperatorSignInEligibility.CanSignInAsync(target, fixture.OperatorRoles, CancellationToken.None);
        Assert.True(after);
        Assert.True(await fixture.OperatorRoles.HoldsRoleSeatAsync(target.Id, SiteId, OperatorRoleName, CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_AlreadyHoldingASeat_IsANoOp_AndReportsAlreadyHeldSeat()
    {
        var fixture = CreateFixture();
        var target = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5);
        fixture.Operators.Seed(target);
        fixture.OperatorRoles.SeedSeat(target.Id, OperatorRoleName, holdsSeat: true);

        var result = await fixture.Handler.HandleAsync(Command(target.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.AlreadyHeldSeat);
        Assert.False(result.Value.OverrodeSeatLimit);
        Assert.True(await fixture.OperatorRoles.HoldsRoleSeatAsync(target.Id, SiteId, OperatorRoleName, CancellationToken.None));
        Assert.Empty(fixture.Overrides.Records);
    }

    [Fact]
    public async Task HandleAsync_ForARemovedOperator_IsRefused_AndLeavesTheRowUnchanged()
    {
        var fixture = CreateFixture();
        var target = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5);
        target.Remove(Now);
        fixture.Operators.Seed(target);
        fixture.OperatorRoles.SeedSeat(target.Id, OperatorRoleName, holdsSeat: false);

        var result = await fixture.Handler.HandleAsync(Command(target.Id), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Operator.AlreadyRemoved", result.Error!.Value.Code);
        Assert.False(await fixture.OperatorRoles.HoldsRoleSeatAsync(target.Id, SiteId, OperatorRoleName, CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_WhenTargetNotFoundForThisSite_ReturnsNotFound()
    {
        var fixture = CreateFixture();
        var otherSiteId = new SiteId(Guid.NewGuid());
        var target = new Operator(new OperatorId(Guid.NewGuid()), otherSiteId, OperatorStatus.Offline, capacity: 5);
        fixture.Operators.Seed(target);

        var result = await fixture.Handler.HandleAsync(Command(target.Id), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Operator.NotFound", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WithinTheSeatLimit_Succeeds_WithNoForce_AndWritesNoOverride()
    {
        var fixture = CreateFixture(seatLimit: 3);
        var target = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5);
        fixture.Operators.Seed(target);
        fixture.OperatorRoles.SeedSeat(target.Id, OperatorRoleName, holdsSeat: false);

        var result = await fixture.Handler.HandleAsync(Command(target.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.OverrodeSeatLimit);
        Assert.True(await fixture.OperatorRoles.HoldsRoleSeatAsync(target.Id, SiteId, OperatorRoleName, CancellationToken.None));
        Assert.Empty(fixture.Overrides.Records);
    }

    /// <summary>The seat-limit decision, proven: exceeding the limit with no force is refused, and the
    /// row is left unchanged - the ordinary case stays exactly as capacity-checked as the tenant's own
    /// `ToggleOperatorSeat`.</summary>
    [Fact]
    public async Task HandleAsync_ExceedingTheSeatLimit_WithNoForce_IsRefused_AndLeavesTheRowUnchanged()
    {
        var fixture = CreateFixture(seatLimit: 1);
        fixture.OperatorRoles.SeedSeat(new OperatorId(Guid.NewGuid()), OperatorRoleName, holdsSeat: true);
        var target = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5);
        fixture.Operators.Seed(target);
        fixture.OperatorRoles.SeedSeat(target.Id, OperatorRoleName, holdsSeat: false);

        var result = await fixture.Handler.HandleAsync(Command(target.Id), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Operator.SeatRestoreExceedsLimitRequiresForce", result.Error!.Value.Code);
        Assert.False(await fixture.OperatorRoles.HoldsRoleSeatAsync(target.Id, SiteId, OperatorRoleName, CancellationToken.None));
        Assert.Empty(fixture.Overrides.Records);
    }

    /// <summary>The override, exercised: force plus a real reason succeeds past the limit and records
    /// exactly one override row - the "states plainly that it is overriding it and why" half of this
    /// item's own Done-when.</summary>
    [Fact]
    public async Task HandleAsync_ExceedingTheSeatLimit_WithForceAndAReason_Succeeds_AndRecordsExactlyOneOverride()
    {
        var fixture = CreateFixture(seatLimit: 1);
        fixture.OperatorRoles.SeedSeat(new OperatorId(Guid.NewGuid()), OperatorRoleName, holdsSeat: true);
        var target = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5);
        fixture.Operators.Seed(target);
        fixture.OperatorRoles.SeedSeat(target.Id, OperatorRoleName, holdsSeat: false);

        var result = await fixture.Handler.HandleAsync(Command(target.Id, force: true, reason: ValidReason), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.OverrodeSeatLimit);
        Assert.True(await fixture.OperatorRoles.HoldsRoleSeatAsync(target.Id, SiteId, OperatorRoleName, CancellationToken.None));

        var recorded = Assert.Single(fixture.Overrides.Records);
        Assert.Equal(SiteId, recorded.SiteId);
        Assert.Equal(target.Id, recorded.OperatorId);
        Assert.Equal(PlatformOwnerSubject, recorded.RestoredBy);
        Assert.Equal(ValidReason, recorded.Reason);
        Assert.Equal(Now, recorded.RestoredAt);
    }

    [Fact]
    public async Task HandleAsync_WithForce_ButNoReason_IsRefused_BeforeTouchingTheOperatorRow()
    {
        var fixture = CreateFixture(seatLimit: 1);
        var target = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5);
        fixture.Operators.Seed(target);
        fixture.OperatorRoles.SeedSeat(target.Id, OperatorRoleName, holdsSeat: false);

        var result = await fixture.Handler.HandleAsync(Command(target.Id, force: true, reason: null), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Operator.SeatRestoreReasonRequired", result.Error!.Value.Code);
        Assert.False(await fixture.OperatorRoles.HoldsRoleSeatAsync(target.Id, SiteId, OperatorRoleName, CancellationToken.None));
        Assert.Empty(fixture.Overrides.Records);
    }

    [Fact]
    public async Task HandleAsync_WithForce_AndABlankReason_IsRefused_TheSameAsNoReasonAtAll()
    {
        var fixture = CreateFixture();
        var target = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5);
        fixture.Operators.Seed(target);
        fixture.OperatorRoles.SeedSeat(target.Id, OperatorRoleName, holdsSeat: false);

        var result = await fixture.Handler.HandleAsync(Command(target.Id, force: true, reason: "   "), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Operator.SeatRestoreReasonRequired", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WithForce_AndAnOverlongReason_IsRefused()
    {
        var fixture = CreateFixture();
        var target = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5);
        fixture.Operators.Seed(target);
        fixture.OperatorRoles.SeedSeat(target.Id, OperatorRoleName, holdsSeat: false);
        var overlong = new string(
            'x', Application.UseCases.RestoreOperatorSeatAsOwner.RestoreOperatorSeatAsOwnerHandler.MaxReasonLength + 1);

        var result = await fixture.Handler.HandleAsync(Command(target.Id, force: true, reason: overlong), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Operator.SeatRestoreReasonRequired", result.Error!.Value.Code);
    }

    /// <summary>Force set when the restore stays within the limit is accepted (no new ceremony on that
    /// path - the identical shape `RevokeModuleForSiteAsOwnerHandler`'s own equivalent test proves for
    /// its own override) but writes nothing: nothing was overridden, so there is nothing to attest
    /// to.</summary>
    [Fact]
    public async Task HandleAsync_WithForceAndAReason_WithinTheLimit_Succeeds_ButWritesNoOverride()
    {
        var fixture = CreateFixture(seatLimit: 3);
        var target = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5);
        fixture.Operators.Seed(target);
        fixture.OperatorRoles.SeedSeat(target.Id, OperatorRoleName, holdsSeat: false);

        var result = await fixture.Handler.HandleAsync(Command(target.Id, force: true, reason: ValidReason), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.OverrodeSeatLimit);
        Assert.True(await fixture.OperatorRoles.HoldsRoleSeatAsync(target.Id, SiteId, OperatorRoleName, CancellationToken.None));
        Assert.Empty(fixture.Overrides.Records);
    }
}
