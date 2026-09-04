using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RevokeModuleForSiteAsOwner;

/// <summary>
/// `22-17`: the platform owner's own revoke - the other half of a grant that can be taken back
/// (this item's own brief: "an entitlement that cannot be taken back is not an entitlement"). See
/// <see cref="RevokeModuleForSiteAsOwnerHandler"/>'s own remarks for why this is a deliberately
/// separate command/handler from <c>RevokeModuleForSite</c>. Deliberately carries no
/// <see cref="OperatorId"/> - the platform owner has none.
/// </summary>
public sealed record RevokeModuleForSiteAsOwner(SiteId SiteId, string ModuleKey, string ProvisioningSecret);
