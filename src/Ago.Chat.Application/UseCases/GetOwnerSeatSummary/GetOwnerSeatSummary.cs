using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetOwnerSeatSummary;

/// <summary>`25-181`: the owner console's own "Пользователи" summary line - held/limit for both
/// Operator and Administrator seats on one site, the platform-owner-scoped analogue of
/// <see cref="GetSeatAssignmentSummary.GetSeatAssignmentSummaryHandler"/>'s own tenant-facing read. See
/// <see cref="GetOwnerSeatSummaryHandler"/>'s own remarks for why it carries no
/// <see cref="Application.Abstractions.IPermissionChecker"/> check.</summary>
public sealed record GetOwnerSeatSummary(SiteId SiteId);

/// <summary>`25-181`: held/limit for both seeded roles - the console-facing wire shape
/// <see cref="GetOwnerSeatSummaryHandler"/> returns, each limit already including the platform owner's
/// own live extra (<see cref="Application.Abstractions.IOwnerSeatGrantStore.GetEffectiveExtraAsync"/>) on
/// top of whatever billing currently grants.</summary>
public sealed record OwnerSeatSummaryDto(int OperatorsHeld, int OperatorsLimit, int AdministratorsHeld, int AdministratorsLimit);
