using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.SetOperatorPresence;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.SetOperatorPresence;

public class SetOperatorPresenceHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());

    [Fact]
    public async Task GoOnlineAsync_FlipsAnOfflineOperatorToOnline()
    {
        var operators = new FakeOperatorRepository();
        operators.Seed(new Operator(OperatorId, SiteId, OperatorStatus.Offline, capacity: 5));
        var handler = new SetOperatorPresenceHandler(operators);

        await handler.GoOnlineAsync(new GoOnline(OperatorId), CancellationToken.None);

        var stored = await operators.GetByIdAsync(OperatorId, CancellationToken.None);
        Assert.Equal(OperatorStatus.Online, stored!.Status);
    }

    [Fact]
    public async Task GoOfflineAsync_FlipsAnOnlineOperatorToOffline()
    {
        var operators = new FakeOperatorRepository();
        operators.Seed(new Operator(OperatorId, SiteId, OperatorStatus.Online, capacity: 5));
        var handler = new SetOperatorPresenceHandler(operators);

        await handler.GoOfflineAsync(new GoOffline(OperatorId), CancellationToken.None);

        var stored = await operators.GetByIdAsync(OperatorId, CancellationToken.None);
        Assert.Equal(OperatorStatus.Offline, stored!.Status);
    }

    [Fact]
    public async Task GoOnlineAsync_WhenTheOperatorRowDoesNotExist_Throws()
    {
        var handler = new SetOperatorPresenceHandler(new FakeOperatorRepository());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.GoOnlineAsync(new GoOnline(OperatorId), CancellationToken.None));
    }
}
