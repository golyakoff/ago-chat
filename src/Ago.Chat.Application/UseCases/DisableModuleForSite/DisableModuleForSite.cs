using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.DisableModuleForSite;

/// <summary>
/// `26-316`: the tenant admin's own self-service disable - the off half of the toggle
/// <see cref="EnableModuleForSite.EnableModuleForSite"/> is the on half of. Gated by the handler on
/// <see cref="Permission.SiteConfigure"/> against the caller's own <see cref="SiteId"/>, exactly like the
/// enable side, so it can only ever turn a module off for a site the caller already administers.
///
/// <para><b>Carries no <c>Force</c>/<c>Reason</c>, unlike
/// <see cref="RevokeModuleForSiteAsOwner.RevokeModuleForSiteAsOwner"/>.</b> The owner's revoke needs that
/// asymmetry because an owner can revoke <em>either</em> their own grant <em>or</em> a tenant's own
/// self-service one, and the second needs a stated reason. A tenant disabling from here has no such
/// choice: the handler refuses outright to touch a row a platform owner granted
/// (<see cref="Domain.EnabledModule.GrantedByOwner"/>), so the only thing this command can ever turn off
/// is a self-service grant the same tenant turned on - no reason to state, and the owner's override stays
/// exactly that.</para>
/// </summary>
public sealed record DisableModuleForSite(OperatorId RequestedBy, SiteId SiteId, string ModuleKey);
