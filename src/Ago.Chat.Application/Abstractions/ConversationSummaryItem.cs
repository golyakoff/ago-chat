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
public sealed record ConversationSummaryItem(
    ConversationId Id,
    VisitorId VisitorId,
    OperatorId? OperatorId,
    string State,
    DateTimeOffset CreatedAt,
    int OperatorUnreadCount,
    string Outcome = nameof(Domain.ConversationOutcome.Unset),
    string? OperatorName = null);
