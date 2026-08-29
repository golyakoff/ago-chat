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
        Application.UseCases.RemoveOperator.RemoveOperatorHandler Handler, FakeOperatorRepository Operators, FakeOutboxWriter Outbox);

    private static Fixture CreateFixture(bool grantPermission = true)
    {
        var operators = new FakeOperatorRepository();
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(RequestedBy, SiteId, Permission.SiteManageOperators);
        }

        var outbox = new FakeOutboxWriter();
        var handler = new Application.UseCases.RemoveOperator.RemoveOperatorHandler(
            operators, permissions, outbox, new FakeIdGenerator(), new FakeClock(Now));
        return new Fixture(handler, operators, outbox);
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
}
