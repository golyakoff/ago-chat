using System.Text.Json;

namespace Ago.Chat.Contracts;

/// <summary>
/// `5-11`: shared by every handler that pre-serializes a `Contracts` DTO into a JSON *string* for
/// <c>INodeFanoutPublisher.PublishAsync</c>, instead of handing SignalR a real POCO to serialize
/// itself. SignalR's own hub protocol uses `camelCase` property names by default (no
/// `AddJsonProtocol` override in `Ago.Chat.Api/Program.cs`) - a hub method that sends a POCO
/// directly (`VisitorHub`/`OperatorHub`'s own local-echo `SendAsync(method, dto, ...)`) gets that for
/// free. A pre-serialized string does not: `Ago.Chat.Api.Realtime.SignalRConnectionDispatcher`
/// re-parses it into a `JsonElement` before handing it to SignalR, and a `JsonElement` re-emits its
/// own already-baked-in property names verbatim, never consulting the hub protocol's naming policy
/// again. Found live in `5-11`: every fan-out-delivered field arrived `undefined` client-side until
/// this was applied, even though the identical local-echo path for the same DTO shape was always
/// correct.
/// </summary>
public static class WireJsonOptions
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}
