namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// Dapper's raw row shape for <see cref="ConversationReadStore.GetConversationsForPersonAsync"/> - a
/// top-level type for the same Dapper-constructor-binding reason <see cref="VisitorHistoryRow"/> is.
/// Every instant is <see cref="DateTime"/>, not <see cref="DateTimeOffset"/>, for the identical reason
/// that type gives: Npgsql over raw ADO.NET/Dapper, not EF's own provider.
/// <see cref="ConversationReadStore"/> converts every one of them before this type crosses back over
/// <c>IConversationReadStore</c>. <see cref="ClosedAt"/> is nullable - a conversation still open has
/// none; <see cref="LastActivityAt"/> is not - the query itself falls back to the conversation's own
/// <c>created_at</c> for one with no messages yet.
/// </summary>
internal sealed record PersonConversationRow(Guid Id, string State, DateTime StartedAt, DateTime? ClosedAt, DateTime LastActivityAt);
