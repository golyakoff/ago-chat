using Ago.Platform.Abstractions;
using Ago.Platform.Realtime;

namespace Ago.Chat.Api.Realtime;

/// <summary>
/// The register/unregister half of 3-01's hub wiring, factored out of <c>VisitorHub</c>/
/// <c>OperatorHub</c> so it is directly testable with plain <see cref="ConnectionId"/>/
/// <see cref="PrincipalKey"/> values - no <c>HubCallerContext</c> needed, since extracting those two
/// values from <c>Context</c> is the only part that genuinely differs between the two hubs.
/// </summary>
public sealed class HubConnectionRegistration(
    IConnectionRegistry connectionRegistry,
    LocalConnectionTracker connectionTracker,
    NodeId currentNode)
{
    public Task OnConnectedAsync(ConnectionId connectionId, PrincipalKey principal, CancellationToken cancellationToken)
    {
        connectionTracker.Add(connectionId, principal);
        return connectionRegistry.RegisterAsync(connectionId, currentNode, principal, cancellationToken);
    }

    public Task OnDisconnectedAsync(ConnectionId connectionId, PrincipalKey principal)
    {
        connectionTracker.Remove(connectionId);
        // Deliberately CancellationToken.None, not the caller's token: by the time a hub calls this
        // from OnDisconnectedAsync, Context.ConnectionAborted may already be signalled, and this
        // cleanup must still complete rather than being cancelled by the very disconnect that
        // triggered it.
        return connectionRegistry.UnregisterAsync(connectionId, currentNode, principal, CancellationToken.None);
    }
}
