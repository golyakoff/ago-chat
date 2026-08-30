using System.Text.Json.Serialization;

namespace Ago.Chat.Infrastructure.YandexGpt;

/// <summary>
/// `19-01`: YandexGPT's own wire shapes - kept in this project alone
/// (`ChannelPortTests.NoProviderVocabulary_AppearsAboveInfrastructure`'s own discipline for a channel
/// provider, extended here to an LLM provider for the same reason: nothing above the Infrastructure
/// boundary may know YandexGPT's own JSON field names). <b>Not confirmed against a real YandexGPT
/// call</b> - the same honest limit `YooKassaDtos`' own remarks state for ЮKassa: this environment has
/// no live API key/folder id and no network access to Yandex Cloud's own documentation host, so every
/// shape below is built from the well-known, publicly documented Yandex Cloud Foundation Models
/// `textGeneration/completion` contract as of this item's own knowledge cutoff. The field names and the
/// terminal/transient status-code split are the parts most likely to need a real-credential correction
/// - see this item's own report for exactly which claims are asserted here versus verified.
/// </summary>
public sealed record YandexGptCompletionRequest(
    [property: JsonPropertyName("modelUri")] string ModelUri,
    [property: JsonPropertyName("completionOptions")] YandexGptCompletionOptions CompletionOptions,
    [property: JsonPropertyName("messages")] IReadOnlyList<YandexGptMessage> Messages);

public sealed record YandexGptCompletionOptions(
    [property: JsonPropertyName("stream")] bool Stream,
    [property: JsonPropertyName("temperature")] double Temperature,
    [property: JsonPropertyName("maxTokens")] string MaxTokens);

/// <summary><see cref="Role"/> is one of Yandex Cloud's own documented three (<c>system</c>,
/// <c>user</c>, <c>assistant</c>) - <see cref="YandexGptReplyDraftClient"/> is the only place in this
/// codebase that maps <c>Ago.Chat.Application.Abstractions.ReplyDraftAuthorKind</c> onto this
/// vocabulary, so nothing above Infrastructure needs to know it exists.</summary>
public sealed record YandexGptMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("text")] string Text);

public sealed record YandexGptCompletionResponse(
    [property: JsonPropertyName("result")] YandexGptCompletionResult? Result);

public sealed record YandexGptCompletionResult(
    [property: JsonPropertyName("alternatives")] IReadOnlyList<YandexGptAlternative>? Alternatives);

public sealed record YandexGptAlternative(
    [property: JsonPropertyName("message")] YandexGptMessage? Message,
    [property: JsonPropertyName("status")] string? Status);

/// <summary>Yandex Cloud's own documented error envelope for a client-shaped (400/401/403/404) refusal
/// - <see cref="Message"/> is the human-readable half, the same role `YooKassaErrorResponse.Description`
/// plays for ЮKassa.</summary>
public sealed record YandexGptErrorResponse(
    [property: JsonPropertyName("code")] int? Code,
    [property: JsonPropertyName("message")] string? Message);
