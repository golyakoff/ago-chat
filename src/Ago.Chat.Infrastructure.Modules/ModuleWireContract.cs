using System.Text.Json.Serialization;

namespace Ago.Chat.Infrastructure.Modules;

/// <summary>
/// `20-07`: the exact bytes of the wire contract recorded in the backlog item and hand-synchronized
/// with `ago-calendar`'s own side - plain HTTP+JSON, camelCase, System.Text.Json (ASP.NET Core Minimal
/// API's own default), never re-derived or approximated at this end.
/// </summary>
internal sealed record StartTaskWireRequest(
    [property: JsonPropertyName("chatTaskId")] Guid ChatTaskId,
    [property: JsonPropertyName("siteId")] Guid SiteId,
    [property: JsonPropertyName("conversationId")] Guid ConversationId,
    [property: JsonPropertyName("triggerText")] string TriggerText);

internal sealed record StartTaskWireResponse(
    [property: JsonPropertyName("externalTaskId")] string ExternalTaskId,
    [property: JsonPropertyName("step")] StepWireDto Step,
    [property: JsonPropertyName("complete")] bool Complete);

internal sealed record SubmitReplyWireRequest(
    [property: JsonPropertyName("chatTaskId")] Guid ChatTaskId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("value")] string Value);

internal sealed record SubmitReplyWireResponse(
    [property: JsonPropertyName("step")] StepWireDto? Step,
    [property: JsonPropertyName("complete")] bool Complete);

/// <summary><see cref="Payload"/> is captured as a raw <see cref="System.Text.Json.JsonElement"/>,
/// never deserialised into a shape this project invents - <see cref="HttpModuleGateway"/> reads its
/// exact original bytes back out via <c>GetRawText()</c> before handing them to
/// <see cref="Domain.MessagePayload"/>'s own constructor, matching that type's own "stored verbatim,
/// not re-serialised" rule even across this one extra JSON round trip.</summary>
internal sealed record StepWireDto(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("payload")] System.Text.Json.JsonElement? Payload,
    [property: JsonPropertyName("actions")] IReadOnlyList<ActionWireDto>? Actions);

internal sealed record ActionWireDto(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("value")] string Value);
