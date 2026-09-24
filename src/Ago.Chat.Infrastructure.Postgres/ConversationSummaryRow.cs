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
    string? OperatorName = null,
    // `25-56`: additive - both call sites now join `visitors` in and select these two.
    string? EmojiCreature = null,
    string? EmojiFood = null,
    // `25-56`'s own second half: additive - both call sites now also `left join lateral` against
    // `visitor_contact_details` for this visitor's own most recent `Name`-kind row's `value`. `left`,
    // not `inner` - most visitors have never given one, and that is a real, common case, not an
    // exceptional one.
    string? VisitorName = null,
    // `26-90`: selected by **both** call sites (`AllForSiteSql`/`ByIdSql`), not one - the defaults
    // below exist for source compatibility, never as a shape either query actually produces. Dapper
    // matches a constructor by exact parameter count, so a statement that omitted these would fail to
    // materialize this record at all; `ByIdSql`'s own remarks carry that finding in full.
    // All four last-message columns are null together exactly when the conversation has no messages at
    // all (the `left join lateral` matched nothing), which is what `ConversationReadStore.ToSummaryItem`
    // tests to decide whether to build a `LatestMessageSummary` at all. `LastMessageAt` is `DateTime`,
    // not `DateTimeOffset`, for the same reason `CreatedAt` above is (this type's own doc comment) -
    // converted before it crosses back over the port.
    string? LastMessageBody = null,
    DateTime? LastMessageAt = null,
    string? LastMessageContentKind = null,
    Guid? LastMessageAttachmentId = null,
    int MessageCount = 0);
