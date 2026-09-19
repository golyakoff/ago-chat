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
///
/// <para><b>`25-170`: <see cref="FakeOperatorRoleRepository"/> replaces <see cref="FakePermissionChecker"/>
/// as this handler's second dependency</b> - `OperatorSignInEligibility.CanSignInAsync` no longer
/// composes a permission checker at all, so every operator this file seeds now also seeds its own
/// `operator_roles` seat via <see cref="FakeOperatorRoleRepository.SeedSeat"/>, the same "holds a seat"
/// fact `Operator`'s own pre-`25-170` default gave every account for free.</para>
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
        var operatorRoles = new FakeOperatorRoleRepository();
        operatorRoles.SeedSeat(operatorId, "Operator", holdsSeat: true);
        var handler = new ResolveOperatorIdentityHandler(repository, operatorRoles);

        var result = await handler.HandleAsync(new ResolveOperatorIdentityQuery("keycloak-sub-123"), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(operatorId, result.OperatorId);
        Assert.Equal(siteId, result.SiteId);
    }

    [Fact]
    public async Task HandleAsync_WhenNoOperatorMatches_ReturnsNull()
    {
        var handler = new ResolveOperatorIdentityHandler(new FakeOperatorRepository(), new FakeOperatorRoleRepository());

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
        var operatorRoles = new FakeOperatorRoleRepository();
        operatorRoles.SeedSeat(operatorA, "Operator", holdsSeat: true);
        operatorRoles.SeedSeat(operatorB, "Operator", holdsSeat: true);
        var handler = new ResolveOperatorIdentityHandler(repository, operatorRoles);

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
        var operatorId = new OperatorId(Guid.NewGuid());
        repository.Seed(new Operator(operatorId, siteA, OperatorStatus.Online, capacity: 5, externalSubjectId: "single-tenant-sub"));
        var operatorRoles = new FakeOperatorRoleRepository();
        operatorRoles.SeedSeat(operatorId, "Operator", holdsSeat: true);
        var handler = new ResolveOperatorIdentityHandler(repository, operatorRoles);

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
        var operatorRoles = new FakeOperatorRoleRepository();
        operatorRoles.SeedSeat(operatorId, "Operator", holdsSeat: true);
        var handler = new ResolveOperatorIdentityHandler(repository, operatorRoles);

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
        var operatorA = new OperatorId(Guid.NewGuid());
        var operatorB = new OperatorId(Guid.NewGuid());
        repository.Seed(new Operator(operatorA, new SiteId(Guid.NewGuid()), OperatorStatus.Online, capacity: 5, externalSubjectId: "multi-sub-2"));
        repository.Seed(new Operator(operatorB, new SiteId(Guid.NewGuid()), OperatorStatus.Online, capacity: 5, externalSubjectId: "multi-sub-2"));
        var operatorRoles = new FakeOperatorRoleRepository();
        operatorRoles.SeedSeat(operatorA, "Operator", holdsSeat: true);
        operatorRoles.SeedSeat(operatorB, "Operator", holdsSeat: true);
        var handler = new ResolveOperatorIdentityHandler(repository, operatorRoles);

        var result = await handler.HandleAsync(new ResolveOperatorIdentityQuery("multi-sub-2"), CancellationToken.None);

        Assert.Null(result);
    }

    /// <summary>
    /// `23-71`/`25-170`: the fix's own central case, restated for the one-rule form -
    /// `decisions/0006`'s "the owner and as many operators as are paid for can sign in", still true
    /// today because the founder holds *two* roles from registration. A seatless Administrator whose
    /// Admin-role row still holds its own seat still resolves, proven against both resolution paths
    /// this handler has (`RequestedSiteId` present and absent).
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenTheOperatorsAdminRoleHoldsASeat_StillResolves()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var repository = new FakeOperatorRepository();
        var admin = new Operator(operatorId, siteId, OperatorStatus.Offline, capacity: 5, externalSubjectId: "seatless-operator-role-admin");
        repository.Seed(admin);
        var operatorRoles = new FakeOperatorRoleRepository();
        // Operator-role seat released, Admin-role seat intact - the founder's own two-role shape.
        operatorRoles.SeedSeat(operatorId, "Operator", holdsSeat: false);
        operatorRoles.SeedSeat(operatorId, "Admin", holdsSeat: true);
        var handler = new ResolveOperatorIdentityHandler(repository, operatorRoles);

        var withRequestedSite = await handler.HandleAsync(
            new ResolveOperatorIdentityQuery("seatless-operator-role-admin", siteId), CancellationToken.None);
        var withoutRequestedSite = await handler.HandleAsync(
            new ResolveOperatorIdentityQuery("seatless-operator-role-admin"), CancellationToken.None);

        Assert.NotNull(withRequestedSite);
        Assert.Equal(operatorId, withRequestedSite.OperatorId);
        Assert.NotNull(withoutRequestedSite);
        Assert.Equal(operatorId, withoutRequestedSite.OperatorId);
    }

    /// <summary>
    /// The complementary case - an operator seatless on *every* role it holds stays refused. `25-170`
    /// removed the permission-exemption door entirely: unlike the test above (seatless on one role, not
    /// the other), there is no role left here for a seat to survive on.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenTheOperatorHoldsNoSeatOnAnyRole_ReturnsNull()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var repository = new FakeOperatorRepository();
        repository.Seed(new Operator(
            operatorId, siteId, OperatorStatus.Offline, capacity: 5, externalSubjectId: "seatless-ordinary"));
        var operatorRoles = new FakeOperatorRoleRepository();
        operatorRoles.SeedSeat(operatorId, "Operator", holdsSeat: false);
        var handler = new ResolveOperatorIdentityHandler(repository, operatorRoles);

        var result = await handler.HandleAsync(new ResolveOperatorIdentityQuery("seatless-ordinary", siteId), CancellationToken.None);

        Assert.Null(result);
    }
}
