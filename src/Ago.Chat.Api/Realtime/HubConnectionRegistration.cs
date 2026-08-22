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

    /// <summary>Returns whether <paramref name="principal"/> has zero connections left anywhere,
    /// not just on this node - `4-04`'s operator-disconnect grace period is the one caller that
    /// needs this; `VisitorHub` calls the same method and simply does not act on the result
    /// (visitor-side disconnect handling beyond reconnect/resume, `3-03`, is out of scope there).</summary>
    public async Task<bool> OnDisconnectedAsync(ConnectionId connectionId, PrincipalKey principal)
    {
        connectionTracker.Remove(connectionId);
        // Deliberately CancellationToken.None, not the caller's token: by the time a hub calls this
        // from OnDisconnectedAsync, Context.ConnectionAborted may already be signalled, and this
        // cleanup must still complete rather than being cancelled by the very disconnect that
        // triggered it.
        await connectionRegistry.UnregisterAsync(connectionId, currentNode, principal, CancellationToken.None);
        var remaining = await connectionRegistry.GetConnectionsAsync(principal, CancellationToken.None);
        return remaining.Count == 0;
    }
}
