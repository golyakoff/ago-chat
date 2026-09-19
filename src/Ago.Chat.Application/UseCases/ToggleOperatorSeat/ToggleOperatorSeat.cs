using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.ToggleOperatorSeat;

/// <summary>`13-03`: a site's `Permission.SiteManageOperators` holder assigns or releases one
/// operator's own seat - `decisions/0006`'s "the owner decides which".
///
/// <para><b>`25-170`: <see cref="RoleName"/> - which role's own seat.</b> "Holds a seat" is now a fact
/// about one `(operator, role)` pairing, not the operator account as a whole, so a caller must say which
/// role it means: the seeded `"Operator"` role (the pre-`25-170` shape, unchanged in effect) or the
/// seeded `"Admin"` role (this item's own generalisation - the Admin role gets the identical manual swap
/// control the Operator role already had).</para></summary>
public sealed record ToggleOperatorSeat(OperatorId RequestedBy, SiteId SiteId, OperatorId TargetOperatorId, string RoleName, bool HoldsSeat);
