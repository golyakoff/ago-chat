using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.EnableModuleForSite;

/// <summary>
/// `26-316`: the tenant admin's own self-service module enable - reintroduced. `19-03` had a tenant
/// self-service enable here; `23-83`/`adr/0151` removed it because it required the caller to hold
/// `adr/0095`'s deployment-wide <see cref="ModuleProvisioningSecret"/> in a browser request body, which
/// a tenant must never do. `adr/0150` (secret) and `adr/0154` (entry point) have since moved both of
/// those inputs out of the request body and into <c>Ago.Chat.Api</c>'s own configuration, read by the
/// handler through <see cref="Application.Abstractions.IModuleProvisioningSecretProvider"/> /
/// <see cref="Application.Abstractions.IModuleEntryPointProvider"/>. With nothing secret left for a
/// tenant to hold, the reason the self-service path was removed no longer applies, so `26-316`'s author
/// decision (option в) brings it back as a one-click enable a tenant admin performs for their own site.
///
/// <para><b>Carries no <see cref="OperatorId"/>-free cross-tenant <see cref="SiteId"/> the way
/// <see cref="EnableModuleForSiteAsOwner.EnableModuleForSiteAsOwner"/> does.</b> <see cref="RequestedBy"/>
/// is the caller's own operator id and <see cref="SiteId"/> is the site named on the route; the handler
/// gates the pair through <see cref="Application.Abstractions.IPermissionChecker"/> on
/// <see cref="Permission.SiteConfigure"/>, so this only ever enables a module for a site the caller
/// already administers - never a tenant they merely named. That permission check, not a
/// <c>RequirePlatformOwner</c> policy, is the whole difference in authorisation from the owner command.</para>
///
/// <para><b>Carries no <c>Credential</c> and no <c>ExpiresAt</c>.</b> Unlike the owner grant, a tenant
/// enabling their own module supplies no credential - the handler mints one through
/// <see cref="Application.Abstractions.IModuleCredentialGenerator"/>, the same way
/// <c>RotateModuleCredentialAsOwnerHandler</c> already does - and the grant never expires
/// (<see cref="Domain.EnabledModule.ExpiresAt"/> is always <see langword="null"/>: a tenant who turned
/// their own module on did not buy a trial, exactly as <see cref="Domain.EnabledModule.ExpiresAt"/>'s own
/// remarks describe for a self-service grant).</para>
/// </summary>
/// <param name="TriggerWords">The chat trigger words this module answers to - opaque to
/// <c>Ago.Chat.*</c> (<see cref="Domain.ModuleKey"/>'s own remarks), supplied by the caller because only
/// the console knows what a "calendar" is and what phrase should open it. The widget renders its booking
/// chip from the first of these (`25-131`), so the console sends the site's real booking trigger word.</param>
public sealed record EnableModuleForSite(
    OperatorId RequestedBy, SiteId SiteId, string ModuleKey, IReadOnlyList<string> TriggerWords);
