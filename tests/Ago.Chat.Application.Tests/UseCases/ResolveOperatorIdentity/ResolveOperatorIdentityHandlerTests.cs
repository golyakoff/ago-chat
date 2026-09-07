using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.ResolveOperatorIdentity;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.ResolveOperatorIdentity;

/// <summary>
/// `13-07`/`adr/0068`: the exact resolution algorithm `ResolveOperatorIdentityHandler`'s own doc
/// comment states, proven against all four cases the backlog item names. The first two tests below
/// are this item's own regression proof - "zero behavioural change for an identity that existed
/// before this item" - kept exactly as they were pre-`13-07` (same method names, same assertions);
/// everything else is new.
/// </summary>
public class ResolveOperatorIdentityHandlerTests
{
    [Fact]
    public async Task HandleAsync_WhenAnOperatorMatchesTheExternalSubjectId_ReturnsItsIdAndSite()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var repository = new FakeOperatorRepository();
        repository.Seed(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5, externalSubjectId: "keycloak-sub-123"));
        var handler = new ResolveOperatorIdentityHandler(repository, new FakePermissionChecker());

        var result = await handler.HandleAsync(new ResolveOperatorIdentityQuery("keycloak-sub-123"), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(operatorId, result.OperatorId);
        Assert.Equal(siteId, result.SiteId);
    }

    [Fact]
    public async Task HandleAsync_WhenNoOperatorMatches_ReturnsNull()
    {
        var handler = new ResolveOperatorIdentityHandler(new FakeOperatorRepository(), new FakePermissionChecker());

        var result = await handler.HandleAsync(new ResolveOperatorIdentityQuery("unknown-sub"), CancellationToken.None);

        Assert.Null(result);
    }

    /// <summary>Case 1 of the algorithm, found path: <c>RequestedSiteId</c> present and this identity
    /// really does hold an `operators` row for it - among several, so a wrong implementation that
    /// ignored <c>RequestedSiteId</c> entirely and fell through to "exactly one" would fail this
    /// (there are two), and one that fell through to "more than one -> null" would also fail it
    /// (this must resolve, not go unresolved).</summary>
    [Fact]
    public async Task HandleAsync_WhenRequestedSiteIdMatchesOneOfSeveralTenancies_ReturnsThatOneOnly()
    {
        var repository = new FakeOperatorRepository();
        var siteA = new SiteId(Guid.NewGuid());
        var siteB = new SiteId(Guid.NewGuid());
        var operatorA = new OperatorId(Guid.NewGuid());
        var operatorB = new OperatorId(Guid.NewGuid());
        repository.Seed(new Operator(operatorA, siteA, OperatorStatus.Online, capacity: 5, externalSubjectId: "multi-sub"));
        repository.Seed(new Operator(operatorB, siteB, OperatorStatus.Online, capacity: 5, externalSubjectId: "multi-sub"));
        var handler = new ResolveOperatorIdentityHandler(repository, new FakePermissionChecker());

        var result = await handler.HandleAsync(new ResolveOperatorIdentityQuery("multi-sub", siteB), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(operatorB, result.OperatorId);
        Assert.Equal(siteB, result.SiteId);
    }

    /// <summary>
    /// Case 1, miss path - the tenant-isolation-critical case, `adr/0068`'s own "never misdirect"
    /// invariant. This identity genuinely administers <c>siteA</c>, and asks (via a client-controlled
    /// signal) for a *different* site it does not administer - the handler must refuse, not silently
    /// hand back the one tenancy it does have. A wrong implementation that fell back to "the one row
    /// that does exist" would fail this test by returning non-null.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenRequestedSiteIdMatchesNoTenancyOfThisIdentity_ReturnsNull_NeverFallsBackToADifferentOne()
    {
        var repository = new FakeOperatorRepository();
        var siteA = new SiteId(Guid.NewGuid());
        var siteNotAdministered = new SiteId(Guid.NewGuid());
        repository.Seed(new Operator(
            new OperatorId(Guid.NewGuid()), siteA, OperatorStatus.Online, capacity: 5, externalSubjectId: "single-tenant-sub"));
        var handler = new ResolveOperatorIdentityHandler(repository, new FakePermissionChecker());

        var result = await handler.HandleAsync(
            new ResolveOperatorIdentityQuery("single-tenant-sub", siteNotAdministered), CancellationToken.None);

        Assert.Null(result);
    }

    /// <summary>Case 2, exactly-one path - restated here (redundant with the two `13-07`-predating
    /// tests above, kept as-is) explicitly against the new `IOperatorRepository` shape
    /// (<c>ListByExternalSubjectIdAsync</c>), since that method did not exist before this item and a
    /// regression in it specifically would not necessarily be caught by the two legacy tests if they
    /// were ever rewritten to call the new methods directly instead of through this handler.</summary>
    [Fact]
    public async Task HandleAsync_WhenRequestedSiteIdIsAbsentAndExactlyOneTenancyExists_ReturnsIt()
    {
        var repository = new FakeOperatorRepository();
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        repository.Seed(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5, externalSubjectId: "single-tenant-sub-2"));
        var handler = new ResolveOperatorIdentityHandler(repository, new FakePermissionChecker());

        var result = await handler.HandleAsync(new ResolveOperatorIdentityQuery("single-tenant-sub-2"), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(operatorId, result.OperatorId);
        Assert.Equal(siteId, result.SiteId);
    }

    /// <summary>
    /// Case 2, more-than-one path - impossible before `13-07` (the old global-unique index made it
    /// so), and the one genuinely new unresolved case this item adds. Guessing which of the two
    /// tenancies to use - "pick the first", "pick the most recently created" - would be exactly the
    /// cross-tenant misdirection this handler's own doc comment refuses; the only honest answer with
    /// no site requested and more than one candidate is "unresolved", the same answer zero tenancies
    /// already produces.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenRequestedSiteIdIsAbsentAndMoreThanOneTenancyExists_ReturnsNull_NeverGuesses()
    {
        var repository = new FakeOperatorRepository();
        repository.Seed(new Operator(
            new OperatorId(Guid.NewGuid()), new SiteId(Guid.NewGuid()), OperatorStatus.Online, capacity: 5, externalSubjectId: "multi-sub-2"));
        repository.Seed(new Operator(
            new OperatorId(Guid.NewGuid()), new SiteId(Guid.NewGuid()), OperatorStatus.Online, capacity: 5, externalSubjectId: "multi-sub-2"));
        var handler = new ResolveOperatorIdentityHandler(repository, new FakePermissionChecker());

        var result = await handler.HandleAsync(new ResolveOperatorIdentityQuery("multi-sub-2"), CancellationToken.None);

        Assert.Null(result);
    }

    /// <summary>
    /// `23-71`: the fix's own central case - `decisions/0006`'s "the owner and as many operators as
    /// are paid for can sign in", restored. An operator row with `HoldsSeat: false` used to resolve to
    /// no claim at all, unconditionally; it now still resolves when the row holds this site's own
    /// `site:manage_operators` permission, proven against both resolution paths this handler has
    /// (`RequestedSiteId` present and absent) so neither is left conflating "holds a seat" with "may
    /// administer this account".
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenTheOperatorHoldsNoSeatButManagesOperators_StillResolves()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var repository = new FakeOperatorRepository();
        var admin = new Operator(
            operatorId, siteId, OperatorStatus.Offline, capacity: 5, externalSubjectId: "seatless-admin", holdsSeat: false);
        repository.Seed(admin);
        var permissions = new FakePermissionChecker();
        permissions.Grant(operatorId, siteId, Permission.SiteManageOperators);
        var handler = new ResolveOperatorIdentityHandler(repository, permissions);

        var withRequestedSite = await handler.HandleAsync(
            new ResolveOperatorIdentityQuery("seatless-admin", siteId), CancellationToken.None);
        var withoutRequestedSite = await handler.HandleAsync(
            new ResolveOperatorIdentityQuery("seatless-admin"), CancellationToken.None);

        Assert.NotNull(withRequestedSite);
        Assert.Equal(operatorId, withRequestedSite.OperatorId);
        Assert.NotNull(withoutRequestedSite);
        Assert.Equal(operatorId, withoutRequestedSite.OperatorId);
    }

    /// <summary>
    /// The complementary case - an ordinary operator (no `site:manage_operators` grant) whose seat was
    /// released stays refused, exactly as `13-03` shipped it. `23-71` widens who may sign in without a
    /// seat; it does not remove the seat requirement for anyone who is not an administrator.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenTheOperatorHoldsNoSeatAndDoesNotManageOperators_ReturnsNull()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var repository = new FakeOperatorRepository();
        repository.Seed(new Operator(
            operatorId, siteId, OperatorStatus.Offline, capacity: 5, externalSubjectId: "seatless-ordinary", holdsSeat: false));
        var handler = new ResolveOperatorIdentityHandler(repository, new FakePermissionChecker());

        var result = await handler.HandleAsync(new ResolveOperatorIdentityQuery("seatless-ordinary", siteId), CancellationToken.None);

        Assert.Null(result);
    }
}
