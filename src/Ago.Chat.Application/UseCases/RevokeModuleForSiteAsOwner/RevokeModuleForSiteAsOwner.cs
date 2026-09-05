using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RevokeModuleForSiteAsOwner;

/// <summary>
/// `22-17`: the platform owner's own revoke - the other half of a grant that can be taken back
/// (this item's own brief: "an entitlement that cannot be taken back is not an entitlement"). See
/// <see cref="RevokeModuleForSiteAsOwnerHandler"/>'s own remarks for why this is a deliberately
/// separate command/handler from <c>RevokeModuleForSite</c>. Deliberately carries no
/// <see cref="OperatorId"/> - the platform owner has none.
///
/// <para><b>`23-13`: <see cref="Force"/> and <see cref="Reason"/> are the asymmetry itself.</b>
/// Revoking a grant the owner made is unchanged - both default to "not forcing", so that path carries
/// no new ceremony. Revoking a tenant's own self-service purchase (<see cref="EnabledModule.GrantedByOwner"/>
/// <see langword="false"/>) is refused unless <see cref="Force"/> is <see langword="true"/> and
/// <see cref="Reason"/> is a real, non-blank justification - see the handler's own remarks for exactly
/// where that is checked and why. Deliberately plain <see langword="bool"/>/<see langword="string"/><c>?</c>,
/// not the <see langword="required"/>-nullable trick <see cref="EnableModuleForSiteAsOwner.EnableModuleForSiteAsOwner"/>'s
/// own <c>ExpiresAt</c> uses: that trick forces a caller to *state* one field regardless of its value,
/// which is right for an expiry (omitting it is ambiguous - forgot, or meant "forever"?) and wrong here
/// (omitting <see cref="Force"/> is not ambiguous - it unambiguously means "not forcing", which is
/// always the safe reading and must never itself require ceremony).</para>
///
/// <para><b><see cref="RevokedBy"/> is recorded, never authorising</b> - the realm role behind
/// <c>RequirePlatformOwner</c> is the entire access-control story, exactly as it was before this item.
/// This is the Keycloak <c>sub</c> claim off the caller's own validated token
/// (<c>OwnerModuleEndpoints.HandleRevokeAsync</c>'s own remarks), carried in the command the same way
/// every site-scoped endpoint already carries an operator's <c>GetOperatorId()</c> - not because it
/// authorises anything here (it does not; <see cref="Ago.Chat.Application.Abstractions.IPermissionChecker"/>
/// is still never called), but because <see cref="Application.Abstractions.IModuleRevokeOverrideRepository"/>'s
/// own row needs a "who" and the platform owner carries no <see cref="OperatorId"/> a domain type could
/// name them by (`adr/0032`).</para>
/// </summary>
public sealed record RevokeModuleForSiteAsOwner(
    SiteId SiteId, string ModuleKey, string ProvisioningSecret, string RevokedBy, bool Force = false, string? Reason = null);
