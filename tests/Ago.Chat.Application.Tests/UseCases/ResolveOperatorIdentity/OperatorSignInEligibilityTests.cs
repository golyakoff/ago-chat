using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.RemoveOperator;
using Ago.Chat.Application.UseCases.ResolveOperatorIdentity;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.ResolveOperatorIdentity;

/// <summary>
/// `23-67`: "no action may leave a tenant with nobody able to sign in and manage operators" is a
/// property of <see cref="OperatorSignInEligibility.CanSignInAsync"/>'s own result, not of any one
/// handler - so these tests drive the actual sign-in check the way <c>ResolveOperatorIdentityHandler</c>
/// does, rather than re-deriving "would this be safe" from a handler's own success/failure code the way
/// `RemoveOperatorHandlerTests`/`ToggleOperatorSeatHandlerTests` each already do for their own action.
///
/// <para><b>`25-170`: the permission-exemption mechanism `23-71` built is gone; these tests are rewritten
/// to prove the one-rule replacement, not the retired exemption.</b> Before this item, a seatless
/// Administrator could still sign in because <c>Operator.CanSignIn</c> took a second,
/// permission-derived door (<c>holdsManageOperatorsPermission</c>). That parameter no longer exists -
/// `CanSignIn` is now `HoldsAnySeatAsync`'s own boolean, full stop. `23-67`'s own incident (a sole
/// manager releasing their own seat) is still closed today, but by a different, more literal mechanism:
/// the account's own founder holds *two* roles from registration, so releasing one role's seat still
/// leaves the other's. An account seatless on *every* role it holds can no longer sign in at all, even
/// if it is the site's last `site:manage_operators` holder - a real, deliberate consequence of removing
/// the exemption, closed instead by the platform owner's own recovery tool
/// (`RestoreOperatorSeatAsOwnerHandler`, `23-68`), not by a second sign-in door.</para>
/// </summary>
public class OperatorSignInEligibilityTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());

    /// <summary>
    /// `23-67`'s own incident, reproduced under the `25-170` mechanism: a site's sole
    /// `site:manage_operators` holder is the founder, holding both seeded roles from registration
    /// (`SiteRegistrationRepository`'s own remarks). Releasing the Operator role's own seat
    /// (`ToggleOperatorSeatHandler`'s entire effect for a toggle-off request) leaves the Admin role's
    /// own seat untouched, so <see cref="OperatorSignInEligibility.CanSignInAsync"/> still resolves true -
    /// proven against the real <see cref="Operator"/>/<see cref="OperatorSignInEligibility"/>, not
    /// reasoned about.
    /// </summary>
    [Fact]
    public async Task CanSignInAsync_TheFoundersSoleManager_SurvivesReleasingTheOperatorRolesOwnSeat()
    {
        var sole = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5);
        var operatorRoles = new FakeOperatorRoleRepository();
        operatorRoles.SeedSeat(sole.Id, "Operator", holdsSeat: true);
        operatorRoles.SeedSeat(sole.Id, "Admin", holdsSeat: true);

        Assert.True(await OperatorSignInEligibility.CanSignInAsync(sole, operatorRoles, CancellationToken.None));

        // The entire effect ToggleOperatorSeatHandler has on operator_roles for a toggle-off request on
        // the Operator role specifically - see that handler's own HandleAsync, which never guards this
        // path.
        await operatorRoles.SetHoldsSeatAsync(sole.Id, SiteId, "Operator", holdsSeat: false, CancellationToken.None);

        Assert.True(
            await OperatorSignInEligibility.CanSignInAsync(sole, operatorRoles, CancellationToken.None),
            "the Admin role's own seat must still let the founder sign in after releasing the Operator " +
            "role's own seat - if this fails, 23-67's incident is reachable again.");
    }

    /// <summary>
    /// `25-170`'s own deliberate consequence, proven rather than assumed: an account seatless on *every*
    /// role it holds cannot sign in at all, even when it is the site's last `site:manage_operators`
    /// holder - the permission-exemption door `23-71` built no longer exists. This is not a regression
    /// left unnoticed; it is the accepted trade-off this item's own design states explicitly, and the
    /// platform owner's own recovery tool (`RestoreOperatorSeatAsOwnerHandler`, `23-68`) exists
    /// specifically for this shape of lockout.
    /// </summary>
    [Fact]
    public async Task CanSignInAsync_ASoleManagerSeatlessOnEveryRole_CannotSignIn()
    {
        var sole = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5);
        var permissions = new FakePermissionChecker();
        permissions.Grant(sole.Id, SiteId, Permission.SiteManageOperators);
        var operators = new FakeOperatorRepository();
        operators.Seed(sole);
        var operatorRoles = new FakeOperatorRoleRepository();
        operatorRoles.SeedSeat(sole.Id, "Admin", holdsSeat: false);

        var handler = new RemoveOperatorHandler(
            operators, permissions, new FakeUnitOfWork(), new FakeOutboxWriter(), new FakeIdGenerator(),
            new FakeClock(new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero)));

        var result = await handler.HandleAsync(
            new Ago.Chat.Application.UseCases.RemoveOperator.RemoveOperator(sole.Id, SiteId, sole.Id), CancellationToken.None);

        // `23-26`'s own guard still protects the role assignment - removal is refused, so the account
        // still exists and still holds the role that grants site:manage_operators.
        Assert.True(result.IsFailure);
        Assert.Equal("Operator.IsLastManager", result.Error!.Value.Code);
        Assert.Null(sole.RemovedAt);

        // But existing and holding the permission is no longer the same as being able to sign in - the
        // account is genuinely locked out until its own seat (or another role's) is restored.
        Assert.False(await OperatorSignInEligibility.CanSignInAsync(sole, operatorRoles, CancellationToken.None));
    }
}
