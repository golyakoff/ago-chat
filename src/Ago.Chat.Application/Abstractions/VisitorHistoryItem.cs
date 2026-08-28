using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `18-07`: one row of <see cref="IConversationReadStore.GetVisitorHistoryAsync"/> - a plain
/// projection, the same "read store returns rows, not aggregates" shape <see cref="MessageHistoryItem"/>
/// and <see cref="ConversationSummaryItem"/> already establish (adr/0004).
///
/// The preview fields describe the conversation's <em>last</em> message, not its first - an operator
/// skimming prior conversations is closer served by "how did this end" than "how did this start",
/// and the last row is also the cheaper of the two to fetch alongside a newest-first keyset scan
/// (a `LEFT JOIN LATERAL ... ORDER BY sequence DESC LIMIT 1`, the same per-row lateral shape as the
/// alternative would need for "first" - no query-shape argument favoured one over the other, so this
/// picked the more useful one).
/// </summary>
public sealed record VisitorHistoryItem(
    ConversationId Id,
    string State,
    DateTimeOffset StartedAt,
    DateTimeOffset? ClosedAt,
    string? PreviewBody,
    MessageAuthorKind? PreviewAuthorKind,
    DateTimeOffset? PreviewCreatedAt);
