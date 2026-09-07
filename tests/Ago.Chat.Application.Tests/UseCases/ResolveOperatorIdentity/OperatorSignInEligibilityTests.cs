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
/// `RemoveOperatorHandlerTests` and `ToggleOperatorSeatHandlerTests` each already do for their own
/// action. `23-67`'s own incident - the sole operator released their own seat and could not sign back
/// in - is reproduced here against the exact pre-`23-71` rule (<see cref="Operator.HoldsSeat"/> alone)
/// to show it fails, then against today's rule to show `23-71` already closed it.
/// </summary>
public class OperatorSignInEligibilityTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());

    /// <summary>
    /// `23-67`'s own incident, reproduced: a site's sole `site:manage_operators` holder releases their
    /// own last seat (<c>ToggleOperatorSeatHandler.HandleAsync</c>'s entire effect on the aggregate is
    /// this one call - see that handler's own remarks for why it carries no separate guard of its own).
    /// Pre-`23-71`, <see cref="Operator.HoldsSeat"/> was the only door and this operator would be
    /// permanently locked out from inside the product, which is exactly what happened on the live
    /// deployment. `23-71`'s <see cref="Operator.CanSignIn"/> adds the permission door, so the same
    /// release leaves them able to sign in - proven against the real <see cref="Operator"/> and
    /// <see cref="OperatorSignInEligibility"/>, not reasoned about.
    /// </summary>
    [Fact]
    public async Task CanSignInAsync_TheSoleManager_SurvivesReleasingTheirOwnLastSeat()
    {
        var sole = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5, holdsSeat: true);
        var permissions = new FakePermissionChecker();
        permissions.Grant(sole.Id, SiteId, Permission.SiteManageOperators);

        Assert.True(await OperatorSignInEligibility.CanSignInAsync(sole, permissions, CancellationToken.None));

        // The entire effect ToggleOperatorSeatHandler has on this aggregate for a toggle-off request -
        // see that handler's own HandleAsync, which never guards this path.
        sole.ToggleSeat(false);

        Assert.False(sole.HoldsSeat);
        Assert.True(
            await OperatorSignInEligibility.CanSignInAsync(sole, permissions, CancellationToken.None),
            "23-71's permission door must keep the sole manager signed-in-capable after releasing their own seat - " +
            "if this fails, 23-67's incident is reachable again.");
    }

    /// <summary>The pre-`23-71` counterpart of the test above, kept to show the incident's actual
    /// mechanism rather than assert it from a changelog: <see cref="Operator.CanSignIn"/> called with
    /// the permission argument hard-wired to <see langword="false"/> is exactly what "a seat is the
    /// only door" meant, and it reproduces the lockout.</summary>
    [Fact]
    public void CanSignIn_PreOperatorSeatlessSignIn_TheSoleManagerReleasingTheirSeatWouldHaveBeenLockedOut()
    {
        var sole = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5, holdsSeat: true);

        Assert.True(sole.CanSignIn(holdsManageOperatorsPermission: false));

        sole.ToggleSeat(false);

        Assert.False(
            sole.CanSignIn(holdsManageOperatorsPermission: false),
            "this is 23-67's incident: pre-23-71, releasing the last seat left nobody who could sign in.");
    }

    /// <summary>
    /// `23-67`'s gap in the existing `23-26` coverage: `RemoveOperatorHandlerTests` proves a refused
    /// self-removal leaves the count at one holder, but nothing before `23-67` asked whether that
    /// surviving holder can actually sign in - the question `23-67` is actually about. Seatless on
    /// purpose: the sole manager here never held a seat at all (the `23-71` shape, not the pre-`23-71`
    /// one), so this specifically proves the survivor's sign-in comes from the permission door, not
    /// from a seat the refusal happened to leave untouched.
    /// </summary>
    [Fact]
    public async Task CanSignInAsync_TheSurvivingManagerAfterARefusedSelfRemoval_CanStillSignIn()
    {
        var sole = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5, holdsSeat: false);
        var permissions = new FakePermissionChecker();
        permissions.Grant(sole.Id, SiteId, Permission.SiteManageOperators);
        var operators = new FakeOperatorRepository();
        operators.Seed(sole);

        var handler = new RemoveOperatorHandler(
            operators, permissions, new FakeUnitOfWork(), new FakeOutboxWriter(), new FakeIdGenerator(),
            new FakeClock(new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero)));

        var result = await handler.HandleAsync(
            new Ago.Chat.Application.UseCases.RemoveOperator.RemoveOperator(sole.Id, SiteId, sole.Id), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Operator.IsLastManager", result.Error!.Value.Code);
        Assert.Null(sole.RemovedAt);
        Assert.True(
            await OperatorSignInEligibility.CanSignInAsync(sole, permissions, CancellationToken.None),
            "the operator the guard refused to remove must still be able to sign in - a count of one " +
            "holder that could not itself sign in would satisfy 23-26's own check while still leaving " +
            "23-67's invariant broken.");
    }
}
