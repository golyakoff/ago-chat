using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetSeatAssignmentSummary;

/// <summary>`13-03`: "N of M seats assigned", plus the over-seats condition itself - the console
/// surface `decisions/0006`'s own mechanism needs so an owner can see it and act.</summary>
public sealed record GetSeatAssignmentSummary(OperatorId RequestedBy, SiteId SiteId);

/// <summary><see cref="OverSeats"/> is <see cref="HeldSeats"/> &gt; <see cref="SeatLimit"/>, computed
/// here rather than stored anywhere - this item's own Scope: a derived, read-time condition, not a
/// stored flag.</summary>
public sealed record SeatAssignmentSummaryDto(int HeldSeats, int SeatLimit, bool OverSeats);
