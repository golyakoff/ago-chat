namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>`12-02`: the flat row <see cref="PlatformOverviewReadStore"/>'s SQL materializes, mapped
/// to <c>SiteOverviewItem</c> afterwards - the same "raw row type here, domain-typed item in
/// Application" split <see cref="ConversationSummaryRow"/>/<c>ConversationSummaryItem</c> already
/// uses, so Dapper never has to know about <c>SiteId</c>.
///
/// <para><c>DateTime</c>, not <c>DateTimeOffset</c>, for the two timestamps: Npgsql hands back a
/// <c>DateTime</c> with <c>Kind.Utc</c> for `timestamptz`, and this project's own precedent
/// (<see cref="ConversationSummaryRow"/>, <see cref="MessageRow"/>) is to convert once, explicitly,
/// at the mapping boundary rather than let a provider conversion decide the offset
/// (`date-and-time.md`).</para></summary>
internal sealed record SiteOverviewRow(
    Guid Id,
    string Name,
    DateTime? CreatedAt,
    long SeatCount,
    long ConversationCount,
    long RecentMessageCount,
    DateTime? LastMessageAt,
    long AttachmentBytes);
