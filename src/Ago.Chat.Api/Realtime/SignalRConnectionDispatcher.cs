using System.Text.Json;
using Ago.Chat.Api.Hubs;
using Ago.Platform.Abstractions;
using Ago.Platform.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace Ago.Chat.Api.Realtime;

/// <summary>
/// The host-supplied half of `Ago.Platform.Realtime`'s <see cref="ILocalConnectionDispatcher"/> port
/// - the one place that actually knows SignalR, and the one place that knows a connection id belongs
/// to <see cref="VisitorHub"/> or <see cref="OperatorHub"/>. That routing comes from
/// <see cref="LocalConnectionTracker"/>, the same map <see cref="HubConnectionRegistration"/>
/// populates on connect - a connection this process no longer tracks (already disconnected) is not
/// an error, it is exactly the "harmless failed delivery" realtime.md describes for a stale entry.
///
/// Deserializes the payload into <see cref="JsonElement"/>, not a concrete DTO type: this class does
/// not need to know what a <c>MessageDto</c> is, only that it can hand the client a structurally
/// faithful JSON value under whatever method name the publisher chose.
/// </summary>
public sealed class SignalRConnectionDispatcher(
    IHubContext<VisitorHub> visitorHub,
    IHubContext<OperatorHub> operatorHub,
    LocalConnectionTracker tracker) : ILocalConnectionDispatcher
{
    private const string VisitorPrefix = "visitor:";

    public Task DispatchAsync(ConnectionId connectionId, string method, string payloadJson, CancellationToken cancellationToken)
    {
        var principal = tracker.TryGet(connectionId);
        if (principal is null)
        {
            return Task.CompletedTask;
        }

        var payload = JsonSerializer.Deserialize<JsonElement>(payloadJson);
        var isVisitor = principal.Value.Value.StartsWith(VisitorPrefix, StringComparison.Ordinal);
        var client = isVisitor
            ? visitorHub.Clients.Client(connectionId.Value)
            : operatorHub.Clients.Client(connectionId.Value);

        return client.SendAsync(method, payload, cancellationToken);
    }
}
