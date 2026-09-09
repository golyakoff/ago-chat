using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetModuleQuantityImpactPreviewAsOwner;

/// <summary>`23-88`: the console's own poll - "has the module answered the last question asked about
/// this site's module K yet, and what did it say." A plain read; see
/// <see cref="GetModuleQuantityImpactPreviewAsOwnerHandler"/>'s own remarks for the three states this
/// can return.</summary>
public sealed record GetModuleQuantityImpactPreviewAsOwner(SiteId SiteId, string ModuleKey);
