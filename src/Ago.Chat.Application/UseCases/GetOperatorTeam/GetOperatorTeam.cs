using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetOperatorTeam;

/// <summary>
/// `23-22`: "a tenant can see... who works here" - the team screen's own bootstrap read, gated on the
/// identical <see cref="Permission.SiteManageOperators"/> permission every other operator-management
/// route in this handler's siblings already uses
/// (<see cref="Application.UseCases.GetSeatAssignmentSummary.GetSeatAssignmentSummaryHandler"/>,
/// <see cref="Application.UseCases.ToggleOperatorSeat.ToggleOperatorSeatHandler"/>,
/// <see cref="Application.UseCases.RemoveOperator.RemoveOperatorHandler"/>).
/// </summary>
public sealed record GetOperatorTeam(OperatorId RequestedBy, SiteId SiteId);

/// <summary>`25-170`: one role on the wire, and whether that specific pairing currently holds a seat -
/// the wire counterpart of <see cref="Application.Abstractions.OperatorRoleSeatAssignment"/>.</summary>
public sealed record OperatorRoleSeatDto(string RoleName, bool HoldsSeat);

/// <summary>One row on the wire - see <see cref="Application.Abstractions.OperatorTeamMemberItem"/>
/// for what each field means and why <see cref="DisplayName"/>/<see cref="Email"/> can be
/// <see langword="null"/>. `23-72`/`25-170`: <see cref="Roles"/> is what lets the console show who
/// already administers before offering to change anyone's role, and render a per-role seat toggle for
/// whichever roles an operator actually holds.</summary>
public sealed record OperatorTeamMemberDto(
    Guid OperatorId, string? DisplayName, string? Email, IReadOnlyList<OperatorRoleSeatDto> Roles);

public sealed record OperatorTeamResponse(IReadOnlyList<OperatorTeamMemberDto> Operators);
