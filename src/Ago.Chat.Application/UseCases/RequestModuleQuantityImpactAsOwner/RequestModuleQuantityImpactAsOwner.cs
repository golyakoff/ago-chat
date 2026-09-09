using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RequestModuleQuantityImpactAsOwner;

/// <summary>
/// `23-88`: the platform owner asking "how many of module K's own countable things would candidate
/// number Q exceed, before I actually grant it" - never asks the module synchronously
/// (`Domain.ModuleQuantityImpactPreview`'s own remarks), so this command's whole job, like its sibling
/// grant, is making the question durable on this side and telling the module it was asked.
/// </summary>
public sealed record RequestModuleQuantityImpactAsOwner(SiteId SiteId, string ModuleKey, int RequestedQuantity);
