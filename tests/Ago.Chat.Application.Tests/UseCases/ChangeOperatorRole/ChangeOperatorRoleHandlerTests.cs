using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.ChangeOperatorRole;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.ChangeOperatorRole;

public class ChangeOperatorRoleHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId RequestedBy = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    private const string OperatorRoleName = "Operator";
    private const string AdminRoleName = "Admin";
    private static readonly Guid OperatorRoleId = Guid.NewGuid();
    private static readonly Guid AdminRoleId = Guid.NewGuid();

    private sealed record Fixture(
        Application.UseCases.ChangeOperatorRole.ChangeOperatorRoleHandler Handler,
        FakeOperatorRepository Operators,
        FakeRoleRepository Roles,
        FakeOperatorRoleRepository OperatorRoles,
        FakePermissionChecker Permissions,
        FakeSiteRepository Sites,
        FakeUnitOfWork UnitOfWork,
        FakeRoleChangeRecordRepository RoleChangeRecords,
        FakeOutboxWriter Outbox);

    /// <summary>`25-25`: <paramref name="site"/> defaults to a paid tier with room for two
    /// Administrators (`ago-business` decision `0012`'s own "Business" row) - every test in this file
    /// that predates this item promotes at most a second colleague to Admin, which a real Business-tier
    /// site (never a free one, which includes only one) can actually hold. A test that needs a
    /// different ceiling - the free tier's own single administrator, or a site already at its limit -
    /// passes its own <paramref name="site"/> explicitly rather than relying on this default.</summary>
    private static Fixture CreateFixture(bool grantCallerPermission = true, Site? site = null)
    {
        var operators = new FakeOperatorRepository();
        var roles = new FakeRoleRepository();
        roles.Seed(SiteId, OperatorRoleName, OperatorRoleId, [Permission.ConversationRead.Value]);
        roles.Seed(SiteId, AdminRoleName, AdminRoleId, [Permission.SiteManageOperators.Value]);

        var permissions = new FakePermissionChecker();
        if (grantCallerPermission)
        {
            permissions.Grant(RequestedBy, SiteId, Permission.SiteManageOperators);
        }

        var operatorRoles = new FakeOperatorRoleRepository();
        var sites = new FakeSiteRepository();
        sites.Seed(site ?? new Site(SiteId, $"site_{SiteId.Value:N}", [], tier: SubscriptionTierBands.Starter, seatLimit: 5));
        var unitOfWork = new FakeUnitOfWork();
        var roleChangeRecords = new FakeRoleChangeRecordRepository();
        var outbox = new FakeOutboxWriter();

        var handler = new Application.UseCases.ChangeOperatorRole.ChangeOperatorRoleHandler(
            operators, roles, operatorRoles, permissions, sites, unitOfWork, roleChangeRecords, outbox,
            new FakeIdGenerator(), new FakeClock(Now));

        return new Fixture(handler, operators, roles, operatorRoles, permissions, sites, unitOfWork, roleChangeRecords, outbox);
    }

    [Fact]
    public async Task HandleAsync_WhenNotPermitted_ReturnsForbidden()
    {
        var fixture = CreateFixture(grantCallerPermission: false);
        var target = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5);
        fixture.Operators.Seed(target);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ChangeOperatorRole.ChangeOperatorRole(RequestedBy, SiteId, target.Id, AdminRoleName),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenTargetNotFoundForThisSite_ReturnsNotFound()
    {
        var fixture = CreateFixture();
        var otherSiteId = new SiteId(Guid.NewGuid());
        var target = new Operator(new OperatorId(Guid.NewGuid()), otherSiteId, OperatorStatus.Offline, capacity: 5);
        fixture.Operators.Seed(target);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ChangeOperatorRole.ChangeOperatorRole(RequestedBy, SiteId, target.Id, AdminRoleName),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Operator.NotFound", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenTargetAlreadyRemoved_ReturnsAlreadyRemoved()
    {
        var fixture = CreateFixture();
        var target = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5);
        target.Remove(Now - TimeSpan.FromDays(1));
        fixture.Operators.Seed(target);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ChangeOperatorRole.ChangeOperatorRole(RequestedBy, SiteId, target.Id, AdminRoleName),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Operator.AlreadyRemoved", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenNewRoleDoesNotExistOnThisSite_ReturnsRoleNotFound()
    {
        var fixture = CreateFixture();
        var target = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5);
        fixture.Operators.Seed(target);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ChangeOperatorRole.ChangeOperatorRole(RequestedBy, SiteId, target.Id, "Supervisor"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Operator.RoleNotFound", result.Error!.Value.Code);
    }

    /// <summary>`25-25`: promoting a colleague to administrator succeeds when the site's own
    /// `AdminLimit` still has room - a paid (Starter) tier includes two (`ago-business` decision
    /// `0012`), this site already has one administrator (the caller, seeded here as an `"Admin"` role
    /// holder rather than only a granted permission, so the new role-scoped count actually sees them),
    /// and promoting a second brings the count to exactly the limit, not past it.</summary>
    [Fact]
    public async Task HandleAsync_WhenPromotingToAdmin_WithRoomUnderTheLimit_Succeeds_AndLeavesHoldsSeatUntouched()
    {
        var fixture = CreateFixture();
        fixture.OperatorRoles.Seed(RequestedBy, AdminRoleName);
        var target = new Operator(
            new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5,
            externalSubjectId: "sub-target", holdsSeat: true);
        fixture.Operators.Seed(target);
        fixture.OperatorRoles.Seed(target.Id, OperatorRoleName);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ChangeOperatorRole.ChangeOperatorRole(RequestedBy, SiteId, target.Id, AdminRoleName),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(AdminRoleId, fixture.OperatorRoles.CurrentRoleId(target.Id));
        // `23-71`'s separation: a role change never touches the seat this operator already held.
        Assert.True(target.HoldsSeat);
        var record = Assert.Single(fixture.RoleChangeRecords.Recorded);
        Assert.Equal([OperatorRoleName], record.PreviousRoleNames);
        Assert.Equal(AdminRoleName, record.NewRoleName);
        Assert.Equal(RequestedBy, record.ChangedByOperatorId);
        Assert.Equal(target.Id, record.ChangedOperatorId);
        var envelope = Assert.Single(fixture.Outbox.Enqueued);
        Assert.Equal(nameof(Ago.Chat.Contracts.RoleAssignmentsChanged), envelope.Type);
        Assert.Equal(1, fixture.UnitOfWork.TransactionsBegun);
        Assert.Equal(1, fixture.UnitOfWork.TransactionsCommitted);
    }

    /// <summary>`25-25`: the case this item exists to change - this site's own paid tier includes
    /// exactly two administrators (`SubscriptionTierBands.BusinessAdminsIncluded`), both already
    /// assigned, and a third promotion is refused rather than "never refused either" (the test this one
    /// replaces, back when `ChangeOperatorRoleHandler`'s own remarks called an administrator ceiling
    /// here `adr/0151`-forbidden). Restated as its own named test, per that same handler's remarks, so
    /// a future session that reaches for a capacity check here sees this explicit, already-broken case
    /// rather than a silent gap.</summary>
    [Fact]
    public async Task HandleAsync_WhenPromotingToAdmin_OnASiteAlreadyAtItsAdministratorLimit_ReturnsAdminLimitReached()
    {
        var fixture = CreateFixture();
        fixture.OperatorRoles.Seed(RequestedBy, AdminRoleName);
        var secondAdmin = new OperatorId(Guid.NewGuid());
        fixture.OperatorRoles.Seed(secondAdmin, AdminRoleName);
        var target = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5);
        fixture.Operators.Seed(target);
        fixture.OperatorRoles.Seed(target.Id, OperatorRoleName);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ChangeOperatorRole.ChangeOperatorRole(RequestedBy, SiteId, target.Id, AdminRoleName),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Operator.AdminLimitReached", result.Error!.Value.Code);
        Assert.Contains("2", result.Error.Value.Message);
        // `ReplaceRoleAsync` was never called - the same "disposed without a commit, rolls back"
        // outcome `HandleAsync_WhenDemotingTheLastAdministrator_ReturnsIsLastManager`'s own `Assert.Null`
        // already proves for the analogous refusal.
        Assert.Null(fixture.OperatorRoles.CurrentRoleId(target.Id));
        Assert.Empty(fixture.RoleChangeRecords.Recorded);
    }

    /// <summary>`25-41`'s own Done-when, in its own words: "proven by a test that shows both the
    /// refusal (unpaid) and the success (paid) against the identical guard." This is the paid half -
    /// the exact same site, the exact same two Administrators already assigned, the exact same
    /// unmodified guard in <c>ChangeOperatorRoleHandler</c> that just refused the test right above -
    /// the only difference is <see cref="Site.AdminLimit"/> itself, raised from `2` to `3` by a real
    /// <see cref="Site.ActivateSubscription"/> call carrying <c>extraAdministrators: 1</c>, the
    /// identical write <c>AdministratorSlotChangeApplier</c> makes after a real purchase. Confirms the
    /// item's own reading of the codebase: no change to <c>ChangeOperatorRoleHandler</c> itself was
    /// needed, because it already read <see cref="Site.AdminLimit"/>, never a fixed constant.</summary>
    [Fact]
    public async Task HandleAsync_WhenPromotingToAdmin_OnASiteThatHasPurchasedAnExtraAdministratorSlot_Succeeds()
    {
        var paidSite = new Site(SiteId, $"site_{SiteId.Value:N}", [], tier: SubscriptionTierBands.Starter, seatLimit: 5);
        paidSite.ActivateSubscription(SubscriptionTierBands.Starter, 5, extraAdministrators: 1, Now);
        paidSite.ClearDomainEvents();
        Assert.Equal(SubscriptionTierBands.BusinessAdminsIncluded + 1, paidSite.AdminLimit);

        var fixture = CreateFixture(site: paidSite);
        fixture.OperatorRoles.Seed(RequestedBy, AdminRoleName);
        var secondAdmin = new OperatorId(Guid.NewGuid());
        fixture.OperatorRoles.Seed(secondAdmin, AdminRoleName);
        var target = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5);
        fixture.Operators.Seed(target);
        fixture.OperatorRoles.Seed(target.Id, OperatorRoleName);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ChangeOperatorRole.ChangeOperatorRole(RequestedBy, SiteId, target.Id, AdminRoleName),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(AdminRoleId, fixture.OperatorRoles.CurrentRoleId(target.Id));
        Assert.Single(fixture.RoleChangeRecords.Recorded);
    }

    /// <summary>`25-25`: the free tier includes exactly one administrator
    /// (`SubscriptionTierBands.FreeAdminsIncluded`) - the account's own founder, per `ago-business`
    /// decision `0011`. A second promotion on a site still on that tier is refused the identical way a
    /// paid tier's third is above, proving the limit itself (not merely the number two) is read from
    /// the site rather than hard-coded.</summary>
    [Fact]
    public async Task HandleAsync_WhenPromotingToAdmin_OnAFreeTierSiteWithAnAdministratorAlready_ReturnsAdminLimitReached()
    {
        var freeSite = new Site(SiteId, $"site_{SiteId.Value:N}", []);
        Assert.Equal(SubscriptionTierBands.FreeAdminsIncluded, freeSite.AdminLimit);
        var fixture = CreateFixture(site: freeSite);
        fixture.OperatorRoles.Seed(RequestedBy, AdminRoleName);
        var target = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5);
        fixture.Operators.Seed(target);
        fixture.OperatorRoles.Seed(target.Id, OperatorRoleName);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ChangeOperatorRole.ChangeOperatorRole(RequestedBy, SiteId, target.Id, AdminRoleName),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Operator.AdminLimitReached", result.Error!.Value.Code);
    }

    /// <summary>`25-25`'s own independence requirement: promoting a colleague to Administrator must
    /// never move the site's held-*seat* count, the count `ToggleOperatorSeatHandler`/
    /// `GetSeatAssignmentSummaryHandler` gate <see cref="Site.SeatLimit"/> against - only the
    /// Administrator-role count this file's own promotion tests already exercise. Proven here by
    /// promoting a seatless colleague to Admin and confirming <see cref="Operator.HoldsSeat"/> is still
    /// exactly what it was before the call, never flipped to <see langword="true"/> as a side effect of
    /// becoming an administrator.</summary>
    [Fact]
    public async Task HandleAsync_WhenPromotingToAdmin_NeverGrantsOrRevokesTheTargetsOwnSeat()
    {
        var fixture = CreateFixture();
        fixture.OperatorRoles.Seed(RequestedBy, AdminRoleName);
        var target = new Operator(
            new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5, holdsSeat: false);
        fixture.Operators.Seed(target);
        fixture.OperatorRoles.Seed(target.Id, OperatorRoleName);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ChangeOperatorRole.ChangeOperatorRole(RequestedBy, SiteId, target.Id, AdminRoleName),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(target.HoldsSeat);
    }

    [Fact]
    public async Task HandleAsync_WhenDemotingTheLastAdministrator_ReturnsIsLastManager()
    {
        var fixture = CreateFixture();
        var target = new Operator(RequestedBy, SiteId, OperatorStatus.Offline, capacity: 5);
        fixture.Operators.Seed(target);
        fixture.OperatorRoles.Seed(target.Id, AdminRoleName);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ChangeOperatorRole.ChangeOperatorRole(RequestedBy, SiteId, target.Id, OperatorRoleName),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Operator.IsLastManager", result.Error!.Value.Code);
        Assert.Null(fixture.OperatorRoles.CurrentRoleId(target.Id));
    }

    [Fact]
    public async Task HandleAsync_WhenDemotingOneOfTwoAdministrators_Succeeds()
    {
        var fixture = CreateFixture();
        var otherAdmin = new OperatorId(Guid.NewGuid());
        fixture.Permissions.Grant(otherAdmin, SiteId, Permission.SiteManageOperators);
        var target = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5);
        fixture.Permissions.Grant(target.Id, SiteId, Permission.SiteManageOperators);
        fixture.Operators.Seed(target);
        fixture.OperatorRoles.Seed(target.Id, AdminRoleName);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ChangeOperatorRole.ChangeOperatorRole(RequestedBy, SiteId, target.Id, OperatorRoleName),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(OperatorRoleId, fixture.OperatorRoles.CurrentRoleId(target.Id));
    }

    /// <summary>A change that neither grants nor revokes `site:manage_operators` (here: a no-op
    /// Operator-to-Operator change) never counts holders or takes the site-row lock - the same
    /// "skip the count entirely when the target never held the permission" optimisation
    /// `RemoveOperatorHandler`'s own analogous test proves.</summary>
    [Fact]
    public async Task HandleAsync_WhenChangeDoesNotAffectManageOperators_TakesNoLock()
    {
        var fixture = CreateFixture();
        var target = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5);
        fixture.Operators.Seed(target);
        fixture.OperatorRoles.Seed(target.Id, OperatorRoleName);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ChangeOperatorRole.ChangeOperatorRole(RequestedBy, SiteId, target.Id, OperatorRoleName),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, fixture.UnitOfWork.TransactionsBegun);
        Assert.Equal(1, fixture.UnitOfWork.TransactionsCommitted);
    }
}
