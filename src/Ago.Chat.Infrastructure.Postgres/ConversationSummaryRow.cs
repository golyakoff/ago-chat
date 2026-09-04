namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// Dapper's raw row shape for <see cref="ConversationReadStore.GetAllForSiteAsync"/> - a top-level
/// type for the same reason <see cref="MessageRow"/> is one (Dapper's dynamic-method deserializer
/// needs a public constructor it can bind to without nested-type visibility questions).
/// <see cref="CreatedAt"/> is <see cref="DateTime"/>, not <see cref="DateTimeOffset"/>, for the exact
/// reason <see cref="MessageRow"/>'s own doc comment gives (Npgsql over raw Dapper, not EF) -
/// <see cref="ConversationReadStore"/> converts it before this type crosses back over
/// <c>IConversationReadStore</c>.
/// </summary>
internal sealed record ConversationSummaryRow(
    Guid Id, Guid VisitorId, Guid? OperatorId, string State, DateTime CreatedAt, int OperatorUnreadCount,
    // `18-10`: additive - both call sites (`ByIdSql`/`AllForSiteSql`) now select `outcome` too.
    string Outcome = "Unset",
    // `23-02`: additive - only `AllForSiteSql` joins `operators` in and selects this; `ByIdSql` leaves
    // it at its default, Dapper's own column-not-present behaviour for a nullable record parameter.
    string? OperatorName = null);
