using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetSeatAssignmentSummary;

/// <summary>`13-03`: "N of M seats assigned", plus the over-seats condition itself - the console
/// surface `decisions/0006`'s own mechanism needs so an owner can see it and act.</summary>
public sealed record GetSeatAssignmentSummary(OperatorId RequestedBy, SiteId SiteId);

/// <summary>
/// `25-170`: one seeded role's own seat summary - <see cref="OverLimit"/> is
/// <see cref="HeldSeats"/> &gt; <see cref="Limit"/>, computed here rather than stored anywhere (this
/// item's own Scope: a derived, read-time condition, not a stored flag). Generalises the pre-`25-170`
/// flat <c>SeatAssignmentSummaryDto</c> (Operator-role only) to one row per role that carries a seat
/// concept at all, so the console's own over-limit banner and manual toggle can render for the Admin
/// role exactly the way they already did for the Operator role.
/// </summary>
public sealed record RoleSeatAssignmentSummaryDto(string RoleName, int HeldSeats, int Limit, bool OverLimit);

/// <summary>Every role-seat summary this site has - always exactly the two seeded roles today
/// (`"Operator"`, `"Admin"`), in that order, since no third role carries a seat concept yet.</summary>
public sealed record SeatAssignmentSummaryDto(IReadOnlyList<RoleSeatAssignmentSummaryDto> Roles);
