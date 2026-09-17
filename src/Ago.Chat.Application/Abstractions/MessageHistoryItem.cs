using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

public sealed record MessageHistoryItem(
    MessageId Id,
    int Sequence,
    MessageAuthorKind AuthorKind,
    Guid AuthorId,
    string Body,
    DateTimeOffset CreatedAt,
    AttachmentId? AttachmentId = null,
    Guid? ClientMessageId = null,
    // `14-06`: the three structured columns, as the strings the row holds. A read model returns rows,
    // not aggregates (adr/0004), so these stay raw rather than being rebuilt into a MessageContent -
    // and Payload in particular *must* stay raw, because parsing it here would be AGO Chat looking
    // inside a document it is not allowed to understand, on the hottest read in the product.
    string? ContentKind = null,
    string? Payload = null,
    string? Actions = null,
    // `25-119`: appended last, the same additive shape every optional field on this record already
    // follows - null for a visitor-authored row and for an operator-authored one no widget connection
    // has acked yet.
    DateTimeOffset? DeliveredAt = null);
