using Ago.Chat.Api.Realtime;
using Ago.Chat.Application.Realtime;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Realtime;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// 3-01's backlog item, at the ago-chat level: proves the real hub wiring (<see
/// cref="HubConnectionRegistration"/>, exactly what <c>VisitorHub</c>/<c>OperatorHub</c> call from
/// <c>OnConnectedAsync</c>/<c>OnDisconnectedAsync</c>) against a real Redis
/// (Testcontainers) - not a live SignalR connection (no <c>HubCallerContext</c> to fake), since the
/// only hub-specific part is extracting a <see cref="ConnectionId"/>/<see cref="PrincipalKey"/> pair
/// from <c>Context</c>, and this exercises that exact pair shape via
/// <see cref="PrincipalKeys.ForVisitor"/>. A live cross-node proof belongs to 3-02, which cannot
/// avoid standing up real connections anyway.
/// </summary>
[Collection(RedisCollection.Name)]
public sealed class HubConnectionRegistrationTests(RedisFixture fixture)
{
    [Fact]
    public async Task TwoConnectionsFromTheSameVisitor_BothAppear_SimulatingAReconnectBeforeTheOldOneExpires()
    {
        var registration = CreateRegistration();
        var principal = PrincipalKeys.ForVisitor(new VisitorId(Guid.NewGuid()));
        var oldConnection = new ConnectionId(Guid.NewGuid().ToString());
        var newConnection = new ConnectionId(Guid.NewGuid().ToString());
        var registry = CreateRegistry();

        await registration.OnConnectedAsync(oldConnection, principal, CancellationToken.None);
        await registration.OnConnectedAsync(newConnection, principal, CancellationToken.None);

        var connections = await registry.GetConnectionsAsync(principal, CancellationToken.None);
        Assert.Equal(2, connections.Count);
        Assert.Contains(connections, c => c.ConnectionId == oldConnection);
        Assert.Contains(connections, c => c.ConnectionId == newConnection);
    }

    [Fact]
    public async Task AnEntry_WithNoHeartbeat_ExpiresOnItsOwnAndStopsAppearing()
    {
        var ttl = TimeSpan.FromSeconds(1);
        var registration = CreateRegistration(ttl);
        var principal = PrincipalKeys.ForOperator(new OperatorId(Guid.NewGuid()));
        var connectionId = new ConnectionId(Guid.NewGuid().ToString());
        var registry = CreateRegistry(ttl);

        await registration.OnConnectedAsync(connectionId, principal, CancellationToken.None);
        Assert.Single(await registry.GetConnectionsAsync(principal, CancellationToken.None));

        await Task.Delay(TimeSpan.FromSeconds(2)); // no heartbeat in between - proving real expiry

        Assert.Empty(await registry.GetConnectionsAsync(principal, CancellationToken.None));
    }

    [Fact]
    public async Task OnDisconnectedAsync_RemovesTheConnectionImmediately_WithoutWaitingForTtl()
    {
        var registration = CreateRegistration(TimeSpan.FromMinutes(5));
        var principal = PrincipalKeys.ForVisitor(new VisitorId(Guid.NewGuid()));
        var connectionId = new ConnectionId(Guid.NewGuid().ToString());
        var registry = CreateRegistry(TimeSpan.FromMinutes(5));

        await registration.OnConnectedAsync(connectionId, principal, CancellationToken.None);
        await registration.OnDisconnectedAsync(connectionId, principal);

        Assert.Empty(await registry.GetConnectionsAsync(principal, CancellationToken.None));
    }

    private HubConnectionRegistration CreateRegistration(TimeSpan? ttl = null) =>
        new(CreateRegistry(ttl), new LocalConnectionTracker(), new NodeId("test-node"));

    private RedisConnectionRegistry CreateRegistry(TimeSpan? ttl = null) =>
        new(fixture.Multiplexer,
            Options.Create(new ConnectionRegistryOptions { EntryTtl = ttl ?? TimeSpan.FromSeconds(30) }),
            NullLogger<RedisConnectionRegistry>.Instance);
}
