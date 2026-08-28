namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// Dapper's raw row shape for <see cref="ConversationReadStore.GetVisitorHistoryAsync"/> - a
/// top-level type for the same Dapper-constructor-binding reason <see cref="MessageRow"/> and
/// <see cref="ConversationSummaryRow"/> are. Every instant is <see cref="DateTime"/>, not
/// <see cref="DateTimeOffset"/>, for the identical reason those two give: Npgsql over raw
/// ADO.NET/Dapper, not EF's own provider. <see cref="ConversationReadStore"/> converts every one of
/// them before this type crosses back over <c>IConversationReadStore</c>.
///
/// <see cref="ClosedAt"/>/<see cref="PreviewBody"/>/<see cref="PreviewAuthorKind"/>/
/// <see cref="PreviewCreatedAt"/> are all nullable - the first because a conversation still open (or
/// closed before `Conversation.ClosedAt` existed) has none, the last three together because the
/// `LEFT JOIN LATERAL` finds no row for a conversation with zero messages.
/// </summary>
internal sealed record VisitorHistoryRow(
    Guid Id, string State, DateTime StartedAt, DateTime? ClosedAt,
    string? PreviewBody, string? PreviewAuthorKind, DateTime? PreviewCreatedAt);
