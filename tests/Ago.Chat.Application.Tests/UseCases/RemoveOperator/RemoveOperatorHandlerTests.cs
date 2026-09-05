using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.RemoveOperator;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.RemoveOperator;

public class RemoveOperatorHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId RequestedBy = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        Application.UseCases.RemoveOperator.RemoveOperatorHandler Handler,
        FakeOperatorRepository Operators,
        FakeOutboxWriter Outbox,
        FakePermissionChecker Permissions,
        FakeUnitOfWork UnitOfWork);

    private static Fixture CreateFixture(bool grantPermission = true)
    {
        var operators = new FakeOperatorRepository();
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(RequestedBy, SiteId, Permission.SiteManageOperators);
        }

        var outbox = new FakeOutboxWriter();
        var unitOfWork = new FakeUnitOfWork();
        var handler = new Application.UseCases.RemoveOperator.RemoveOperatorHandler(
            operators, permissions, unitOfWork, outbox, new FakeIdGenerator(), new FakeClock(Now));
        return new Fixture(handler, operators, outbox, permissions, unitOfWork);
    }

    [Fact]
    public async Task HandleAsync_WhenNotPermitted_ReturnsForbidden()
    {
        var fixture = CreateFixture(grantPermission: false);
        var target = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5);
        fixture.Operators.Seed(target);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RemoveOperator.RemoveOperator(RequestedBy, SiteId, target.Id), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Null(target.RemovedAt);
    }

    [Fact]
    public async Task HandleAsync_WhenValid_StampsRemovedAt_AndEnqueuesOperatorRemovedFromSite()
    {
        var fixture = CreateFixture();
        var target = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5);
        fixture.Operators.Seed(target);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RemoveOperator.RemoveOperator(RequestedBy, SiteId, target.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(Now, target.RemovedAt);
        var envelope = Assert.Single(fixture.Outbox.Enqueued);
        Assert.Equal(nameof(Ago.Chat.Contracts.OperatorRemovedFromSite), envelope.Type);
    }

    [Fact]
    public async Task HandleAsync_WhenAlreadyRemoved_ReturnsAlreadyRemoved()
    {
        var fixture = CreateFixture();
        var target = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5);
        target.Remove(Now - TimeSpan.FromDays(1));
        target.ClearDomainEvents();
        fixture.Operators.Seed(target);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RemoveOperator.RemoveOperator(RequestedBy, SiteId, target.Id), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Operator.AlreadyRemoved", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenTargetNotFoundForThisSite_ReturnsNotFound()
    {
        var fixture = CreateFixture();
        var otherSiteId = new SiteId(Guid.NewGuid());
        var target = new Operator(new OperatorId(Guid.NewGuid()), otherSiteId, OperatorStatus.Offline, capacity: 5);
        fixture.Operators.Seed(target);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RemoveOperator.RemoveOperator(RequestedBy, SiteId, target.Id), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Operator.NotFound", result.Error!.Value.Code);
    }

    /// <summary>
    /// `23-26`: the invariant's own textbook case - a site's sole `site:manage_operators` holder removes
    /// themselves. Self-removal is legitimate in general (an operator who leaves should not need a
    /// colleague to take them off), but this is the one case where it is also the *only* remaining
    /// holder, so the guard refuses it - the "not you cannot remove yourself" distinction only shows up
    /// once compared against <see cref="HandleAsync_WhenAnotherManagerRemains_RemovingOneManagerSucceeds"/>
    /// below, where the identical self/other distinction does not matter at all.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenCallerRemovesSelf_AndIsTheLastManager_ReturnsRefused()
    {
        var operators = new FakeOperatorRepository();
        var permissions = new FakePermissionChecker();
        var target = new Operator(RequestedBy, SiteId, OperatorStatus.Offline, capacity: 5);
        permissions.Grant(RequestedBy, SiteId, Permission.SiteManageOperators);
        operators.Seed(target);

        var outbox = new FakeOutboxWriter();
        var unitOfWork = new FakeUnitOfWork();
        var handler = new Application.UseCases.RemoveOperator.RemoveOperatorHandler(
            operators, permissions, unitOfWork, outbox, new FakeIdGenerator(), new FakeClock(Now));

        var result = await handler.HandleAsync(
            new Application.UseCases.RemoveOperator.RemoveOperator(RequestedBy, SiteId, target.Id), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Operator.IsLastManager", result.Error!.Value.Code);
        Assert.Null(target.RemovedAt);
        Assert.Empty(outbox.Enqueued);
        // The transaction opened for the count read was rolled back, not committed - FakeUnitOfWork's
        // own remarks on what a handler unit test can prove about atomicity.
        Assert.Equal(1, unitOfWork.TransactionsBegun);
        Assert.Equal(0, unitOfWork.TransactionsCommitted);
    }

    /// <summary>
    /// `23-26`'s other required half: the refusal is about the *site's* invariant, not about who is
    /// asking. Modelled here the way the real <c>PermissionChecker.HasPermissionAsync</c> actually
    /// behaves - it resolves a role assignment, never <see cref="Operator.RemovedAt"/> - so a caller
    /// whose own operator row was itself already removed (e.g. by a concurrent request that landed a
    /// moment earlier; <c>RemoveOperatorConcurrencyTests</c> proves this exact interleaving happens on
    /// real Postgres) still passes the permission gate on a stale-but-unexpired session, and is
    /// genuinely a different identity from the target. The guard refuses all the same, because
    /// <see cref="IPermissionChecker.CountNonRemovedHoldersAsync"/> excludes a removed operator from the
    /// count regardless of whether their own request happens to still be in flight.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenAnotherCallerWhoIsAlreadyRemoved_RemovesTheLastManager_ReturnsRefused()
    {
        var operators = new FakeOperatorRepository();
        var permissions = new FakePermissionChecker();
        var staleCaller = new OperatorId(Guid.NewGuid());
        var target = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5);

        permissions.Grant(staleCaller, SiteId, Permission.SiteManageOperators);
        permissions.MarkRemoved(staleCaller);
        permissions.Grant(target.Id, SiteId, Permission.SiteManageOperators);
        operators.Seed(target);

        var outbox = new FakeOutboxWriter();
        var unitOfWork = new FakeUnitOfWork();
        var handler = new Application.UseCases.RemoveOperator.RemoveOperatorHandler(
            operators, permissions, unitOfWork, outbox, new FakeIdGenerator(), new FakeClock(Now));

        var result = await handler.HandleAsync(
            new Application.UseCases.RemoveOperator.RemoveOperator(staleCaller, SiteId, target.Id), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Operator.IsLastManager", result.Error!.Value.Code);
        Assert.Null(target.RemovedAt);
    }

    /// <summary>
    /// `23-26`'s own "a guard that over-refuses is the likelier bug here" test - two managers, one
    /// removes the other, the legitimate case is untouched. Proves the guard is keyed to "would this
    /// leave zero holders", not "is the caller removing someone other than themselves".
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenAnotherManagerRemains_RemovingOneManagerSucceeds()
    {
        var fixture = CreateFixture();
        var otherManager = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5);
        fixture.Permissions.Grant(otherManager.Id, SiteId, Permission.SiteManageOperators);
        fixture.Operators.Seed(otherManager);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RemoveOperator.RemoveOperator(RequestedBy, SiteId, otherManager.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(Now, otherManager.RemovedAt);
        Assert.Equal(1, fixture.UnitOfWork.TransactionsBegun);
        Assert.Equal(1, fixture.UnitOfWork.TransactionsCommitted);
    }

    /// <summary>Removing an operator who never held `site:manage_operators` never even reads the
    /// holder count - the guard has nothing to protect against here, and the transaction still opens
    /// (this handler's one unconditional path) but never needed the lock <c>CountNonRemovedHoldersAsync</c>
    /// would have taken.</summary>
    [Fact]
    public async Task HandleAsync_WhenTargetNeverHeldTheManagePermission_RemovalSucceeds()
    {
        var fixture = CreateFixture();
        var plainAgent = new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5);
        fixture.Operators.Seed(plainAgent);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RemoveOperator.RemoveOperator(RequestedBy, SiteId, plainAgent.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(Now, plainAgent.RemovedAt);
    }
}
