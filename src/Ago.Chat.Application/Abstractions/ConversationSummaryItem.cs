using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `5-08`: one row of <see cref="IConversationReadStore.GetAllForSiteAsync"/> - a plain projection
/// of the <c>conversations</c> table (own type, not the <see cref="Conversation"/> aggregate, the
/// same "read store returns rows, not aggregates" shape <see cref="MessageHistoryItem"/> already
/// established for the message side).
/// </summary>
/// <param name="Outcome">`18-10`: additive, the same "a caller that never populates it gets the
/// default" wire-contract rule `OperatorId` above already established for this record
/// (`api-design.md`). The CLR member name of <see cref="Domain.ConversationOutcome"/> - a plain
/// projection, not the domain enum itself, matching <paramref name="State"/>'s own shape right above
/// it.</param>
/// <param name="OperatorName">`23-02`: additive, the identical rule - <see langword="null"/> for a row
/// that predates the column, or for a caller (`GetByIdAsync`'s own SQL) that does not join it in at
/// all - only `GetAllForSiteAsync`'s admin site-wide list needs a name today, so it is the only join
/// site.</param>
/// <param name="EmojiCreature">`25-56`: additive, the identical rule <paramref name="OperatorName"/>
/// above already establishes - joined in from `visitors` by both <c>AllForSiteSql</c>/<c>ByIdSql</c>,
/// paired with <paramref name="EmojiFood"/>.</param>
/// <param name="VisitorName">`25-56`'s own second half: the visitor's own name, additive the same way -
/// <see langword="null"/> when the visitor has never given one, or for a row that predates this field.
/// Unlike <paramref name="EmojiCreature"/>/<paramref name="EmojiFood"/> this is not a plain column on
/// `visitors` - both call sites join it from a `left join lateral` against `visitor_contact_details`
/// picking that visitor's own most recent `Name`-kind row (`IVisitorContactDetailRepository
/// .GetNamesForVisitorsAsync`'s own remarks on why "most recent" is the right reduction when more than
/// one exists).</param>
/// <param name="LatestMessage">`26-90`: this conversation's own last message, or <see langword="null"/>
/// when it has none at all - additive, the identical rule every parameter above already follows.
/// Populated by both read-store call sites, not just the list: this record means one thing regardless
/// of which query produced it, and a point lookup quietly reporting "nothing was ever said" for a
/// conversation full of messages would break that (<c>ByIdSql</c>'s own remarks). Deliberately
/// the same <see cref="LatestMessageSummary"/> record
/// <see cref="IConversationReadStore.GetLatestMessagesAsync"/> already returns, rather than four loose
/// fields flattened onto this row: both feed the same wire fields through the same
/// <c>LastMessagePreviewMapper</c>, and a second near-identical shape would be a second chance for the
/// two to disagree about what "the latest message" means.
///
/// <para>Populated inside <c>AllForSiteSql</c>'s own <c>left join lateral</c>, never by a second read
/// after the page comes back - <see cref="IConversationReadStore.GetAllForSiteAsync"/>'s own remarks
/// about `18-04`'s tag filter give the reason: this is the one genuinely paginated list on this table,
/// so anything done after the page was already cut is done to the wrong rows.</para></param>
/// <param name="MessageCount">`26-90`: how many messages this conversation holds in total - a total,
/// never an unread count (<see cref="Contracts.ConversationSummaryDto.MessageCount"/>'s own remarks).
/// <c>0</c> means a conversation with no messages at all; the default exists for source compatibility
/// with callers that construct this record by hand (tests, fakes), never for a read-store query, both
/// of which select it. One conversation's own length is bounded by that conversation - it is not the
/// <c>COUNT(*)</c>-over-the-whole-site tally `26-90`'s own Out of scope rules out.</param>
public sealed record ConversationSummaryItem(
    ConversationId Id,
    VisitorId VisitorId,
    OperatorId? OperatorId,
    string State,
    DateTimeOffset CreatedAt,
    int OperatorUnreadCount,
    string Outcome = nameof(Domain.ConversationOutcome.Unset),
    string? OperatorName = null,
    string? EmojiCreature = null,
    string? EmojiFood = null,
    string? VisitorName = null,
    LatestMessageSummary? LatestMessage = null,
    int MessageCount = 0);
