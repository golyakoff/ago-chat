using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetOperatorPresence;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetOperatorPresence;

public class GetOperatorPresenceHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());

    [Theory]
    [InlineData(OperatorStatus.Offline)]
    [InlineData(OperatorStatus.Online)]
    [InlineData(OperatorStatus.Away)]
    public async Task HandleAsync_ReturnsTheOperatorsCurrentStatus(OperatorStatus status)
    {
        var operators = new FakeOperatorRepository();
        operators.Seed(new Operator(OperatorId, SiteId, status, capacity: 5));
        var handler = new GetOperatorPresenceHandler(operators);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetOperatorPresence.GetOperatorPresence(OperatorId), CancellationToken.None);

        Assert.Equal(status, result);
    }

    [Fact]
    public async Task HandleAsync_WhenTheOperatorRowDoesNotExist_Throws()
    {
        var handler = new GetOperatorPresenceHandler(new FakeOperatorRepository());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(
                new Application.UseCases.GetOperatorPresence.GetOperatorPresence(OperatorId), CancellationToken.None));
    }
}
