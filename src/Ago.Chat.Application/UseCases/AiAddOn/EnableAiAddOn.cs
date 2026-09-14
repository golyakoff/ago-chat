using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.AiAddOn;

/// <summary>`25-04`: turn the AI add-on on for this site, from now. Carries no timestamp - the cut-off
/// is <see cref="Ago.Platform.Kernel.IClock"/>'s own <c>UtcNow</c> inside the handler, never a caller's
/// value, so no caller can enable the add-on "as of" a date in the past (`CLAUDE.md` rule 11, and the
/// exact hole decision 6 exists to close).</summary>
public sealed record EnableAiAddOn(SiteId SiteId, OperatorId EnabledBy);

/// <summary>`25-04`: turn it off. Transmission stops from this call; the enablement row keeps its
/// history (<see cref="AiAddOnEnablement"/>'s own remarks).</summary>
public sealed record DisableAiAddOn(SiteId SiteId, OperatorId DisabledBy);
