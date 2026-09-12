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
    string? VisitorName = null);
