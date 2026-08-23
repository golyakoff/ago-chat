using Ago.Platform.Abstractions;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>`5-07`: the minimal fake `GetVisitorPresenceHandlerTests` needs - only `GetConnectionsAsync`
/// is ever called on the operator-presence-query path, so only it is backed by real state; the write
/// methods are `NotImplementedException` on purpose, so a test that starts relying on them fails loudly
/// instead of silently passing against unimplemented behaviour.</summary>
public sealed class FakeConnectionRegistry : IConnectionRegistry
{
    private readonly Dictionary<PrincipalKey, List<RegisteredConnection>> _connections = [];

    public void SeedConnected(PrincipalKey principal, params RegisteredConnection[] connections) =>
        _connections[principal] = [.. connections];

    public Task<IReadOnlyCollection<RegisteredConnection>> GetConnectionsAsync(PrincipalKey principal, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<RegisteredConnection>>(
            _connections.TryGetValue(principal, out var found) ? found : []);

    public Task RegisterAsync(ConnectionId connectionId, NodeId nodeId, PrincipalKey principal, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task UnregisterAsync(ConnectionId connectionId, NodeId nodeId, PrincipalKey principal, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task RemoveNodeAsync(NodeId nodeId, CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}
