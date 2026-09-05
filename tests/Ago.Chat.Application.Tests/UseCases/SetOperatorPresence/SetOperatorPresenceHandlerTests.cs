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

    // `23-20`
    [Fact]
    public async Task GoAwayAsync_FlipsAnOnlineOperatorToAway()
    {
        var operators = new FakeOperatorRepository();
        operators.Seed(new Operator(OperatorId, SiteId, OperatorStatus.Online, capacity: 5));
        var handler = new SetOperatorPresenceHandler(operators);

        await handler.GoAwayAsync(new GoAway(OperatorId), CancellationToken.None);

        var stored = await operators.GetByIdAsync(OperatorId, CancellationToken.None);
        Assert.Equal(OperatorStatus.Away, stored!.Status);
    }

    [Fact]
    public async Task GoAwayAsync_WhenTheOperatorRowDoesNotExist_Throws()
    {
        var handler = new SetOperatorPresenceHandler(new FakeOperatorRepository());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.GoAwayAsync(new GoAway(OperatorId), CancellationToken.None));
    }

    [Fact]
    public async Task NoteConnectedAsync_WhenOffline_BecomesOnline()
    {
        var operators = new FakeOperatorRepository();
        operators.Seed(new Operator(OperatorId, SiteId, OperatorStatus.Offline, capacity: 5));
        var handler = new SetOperatorPresenceHandler(operators);

        await handler.NoteConnectedAsync(new NoteConnected(OperatorId), CancellationToken.None);

        var stored = await operators.GetByIdAsync(OperatorId, CancellationToken.None);
        Assert.Equal(OperatorStatus.Online, stored!.Status);
    }

    // `23-20`: the defect this item exists to close, at the handler level - OperatorHub.OnConnectedAsync
    // now calls NoteConnectedAsync instead of GoOnlineAsync specifically so this stays true.
    [Fact]
    public async Task NoteConnectedAsync_WhenAway_StaysAway()
    {
        var operators = new FakeOperatorRepository();
        operators.Seed(new Operator(OperatorId, SiteId, OperatorStatus.Away, capacity: 5));
        var handler = new SetOperatorPresenceHandler(operators);

        await handler.NoteConnectedAsync(new NoteConnected(OperatorId), CancellationToken.None);

        var stored = await operators.GetByIdAsync(OperatorId, CancellationToken.None);
        Assert.Equal(OperatorStatus.Away, stored!.Status);
    }

    [Fact]
    public async Task NoteConnectedAsync_WhenTheOperatorRowDoesNotExist_Throws()
    {
        var handler = new SetOperatorPresenceHandler(new FakeOperatorRepository());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.NoteConnectedAsync(new NoteConnected(OperatorId), CancellationToken.None));
    }
}
