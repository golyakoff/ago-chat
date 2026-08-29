using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.ToggleOperatorSeat;

/// <summary>`13-03`: a site's `Permission.SiteManageOperators` holder assigns or releases one
/// operator's own seat - `decisions/0006`'s "the owner decides which".</summary>
public sealed record ToggleOperatorSeat(OperatorId RequestedBy, SiteId SiteId, OperatorId TargetOperatorId, bool HoldsSeat);
