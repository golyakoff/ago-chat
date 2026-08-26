using System.Text.Json;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Mapping;

/// <summary>
/// One read-model row to one wire message.
///
/// <para>Extracted in `14-06` because there were already three copies of this mapping - both hubs'
/// private <c>ToDto</c> and <c>ResolveMessageDeliveryTargetsHandler</c>'s inline one - and a fourth
/// field would have been a fourth chance for two of them to disagree about a message a client sees.
/// The fan-out copy and the local-echo copy of the same message diverging is `5-11`'s own failure
/// mode, found live.</para>
///
/// <para><b>The payload is re-parsed here, and that is transport rather than interpretation.</b>
/// <see cref="MessageDto.Content"/> is a <see cref="JsonElement"/> so a client receives
/// <c>content: {...}</c> and not a JSON string it has to parse a second time out of a document it
/// has just parsed. Producing one costs a parse whose result is written straight back out; no field
/// name is read, no key is looked up, and nothing branches on what is inside. A prose message -
/// which is all of them today - pays nothing, because there is no payload to parse.</para>
/// </summary>
public static class MessageDtoMapper
{
    public static MessageDto ToDto(MessageHistoryItem item, ConversationId conversationId)
    {
        ArgumentNullException.ThrowIfNull(item);

        return new MessageDto(
            item.Id.Value,
            item.Sequence,
            item.AuthorKind.ToString(),
            item.AuthorId,
            item.Body,
            item.CreatedAt,
            item.AttachmentId?.Value,
            item.ClientMessageId,
            conversationId.Value,
            item.ContentKind,
            ParsePayload(item.Payload),
            ParseActions(item.Actions));
    }

    public static IReadOnlyList<MessageDto> ToDtos(
        IReadOnlyList<MessageHistoryItem> items, ConversationId conversationId)
    {
        ArgumentNullException.ThrowIfNull(items);
        return [.. items.Select(item => ToDto(item, conversationId))];
    }

    /// <summary>
    /// <see cref="JsonDocument"/> is disposable and <see cref="JsonElement"/> is a view over its
    /// buffer, so the element is cloned before the document is released - otherwise the DTO would
    /// carry a window onto memory that has been returned to the pool, which serialises as garbage
    /// intermittently rather than failing.
    /// </summary>
    private static JsonElement? ParsePayload(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return null;
        }

        using var document = JsonDocument.Parse(payload);
        return document.RootElement.Clone();
    }

    /// <summary>The actions column, which AGO Chat does own the schema of - unlike the payload above.
    /// Written by <c>MessageContentConverters</c> in the same shape, and deserialised here rather
    /// than being handed on as a string, because a client that has to print a numbered list needs a
    /// list.</summary>
    private static IReadOnlyList<MessageActionDto>? ParseActions(string? actions) =>
        string.IsNullOrEmpty(actions)
            ? null
            : JsonSerializer.Deserialize<List<MessageActionDto>>(actions, StorageJson.Options);

    /// <summary>Matches the options <c>MessageContentConverters</c> writes with. Stated as a shared
    /// constant rather than a repeated literal because the reader and the writer of a stored format
    /// disagreeing about casing is a bug that only appears once a row exists.</summary>
    private static class StorageJson
    {
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    }
}
