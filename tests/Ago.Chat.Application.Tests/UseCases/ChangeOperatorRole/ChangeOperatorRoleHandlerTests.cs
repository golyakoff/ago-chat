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
        FakeUnitOfWork UnitOfWork,
        FakeRoleChangeRecordRepository RoleChangeRecords,
        FakeOutboxWriter Outbox);

    private static Fixture CreateFixture(bool grantCallerPermission = true)
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
        var unitOfWork = new FakeUnitOfWork();
        var roleChangeRecords = new FakeRoleChangeRecordRepository();
        var outbox = new FakeOutboxWriter();

        var handler = new Application.UseCases.ChangeOperatorRole.ChangeOperatorRoleHandler(
            operators, roles, operatorRoles, permissions, unitOfWork, roleChangeRecords, outbox,
            new FakeIdGenerator(), new FakeClock(Now));

        return new Fixture(handler, operators, roles, operatorRoles, permissions, unitOfWork, roleChangeRecords, outbox);
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

    /// <summary>Promoting a colleague to administrator is never refused for capacity - an administrator
    /// is a role, not a purchase (`adr/0151`). This site already has one administrator (the caller);
    /// promoting a second succeeds unconditionally.</summary>
    [Fact]
    public async Task HandleAsync_WhenPromotingToAdmin_Succeeds_AndLeavesHoldsSeatUntouched()
    {
        var fixture = CreateFixture();
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

    /// <summary>A third, a fourth, ... administrator is never refused either - restated as its own test
    /// so a future session that reaches for a capacity check here has an explicit, named case to break
    /// rather than a silent gap.</summary>
    [Fact]
    public async Task HandleAsync_WhenPromotingToAdmin_OnASiteThatAlreadyHasTwoAdministrators_StillSucceeds()
    {
        var fixture = CreateFixture();
        var secondAdmin = new OperatorId(Guid.NewGuid());
        fixture.Permissions.Grant(secondAdmin, SiteId, Permission.SiteManageOperators);
        var target = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5);
        fixture.Operators.Seed(target);
        fixture.OperatorRoles.Seed(target.Id, OperatorRoleName);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ChangeOperatorRole.ChangeOperatorRole(RequestedBy, SiteId, target.Id, AdminRoleName),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(AdminRoleId, fixture.OperatorRoles.CurrentRoleId(target.Id));
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
