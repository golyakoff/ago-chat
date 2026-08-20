using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

public sealed record MessageHistoryItem(
    MessageId Id,
    int Sequence,
    MessageAuthorKind AuthorKind,
    Guid AuthorId,
    string Body,
    DateTimeOffset CreatedAt);
