namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// Dapper's raw row shape for <see cref="ConversationReadStore.GetVisitorSummaryAsync"/> - a top-level
/// type for the same Dapper-constructor-binding reason <see cref="VisitorHistoryRow"/> is.
/// <see cref="FirstSeenAt"/> is <see cref="DateTime"/>, not <see cref="DateTimeOffset"/>, for the
/// identical reason that type gives: Npgsql over raw ADO.NET/Dapper, not EF's own provider.
/// <see cref="ConversationReadStore"/> converts it before this type crosses back over
/// <c>IConversationReadStore</c>.
/// </summary>
internal sealed record VisitorSummaryRow(DateTime FirstSeenAt, int ConversationCount);
