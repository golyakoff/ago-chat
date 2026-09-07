using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.EnableModuleForSiteAsOwner;

/// <summary>
/// `22-17`: the platform owner's own module grant - see
/// <see cref="EnableModuleForSiteAsOwnerHandler"/>'s own remarks for why this is a deliberately
/// separate command/handler from <c>EnableModuleForSite</c> rather than a nullable-<see cref="OperatorId"/>
/// branch on it, the identical shape <c>UnlinkChannelIdentityAsOwner</c>'s own remarks establish for
/// the platform owner's first write surface. Deliberately carries no <see cref="OperatorId"/> - the
/// platform owner has none (`authorization.md`'s own actor table).
/// </summary>
/// <param name="SiteId">The tenant being granted the module - named directly by the owner, unlike
/// every operator-gated caller in this codebase, where a site is either the caller's own or reached
/// through a resource the caller already owns. This is exactly what makes the call a *deliberate
/// cross-tenant write* (this item's own brief) rather than a bug: the caller may name any tenant, and
/// the sole reason that is safe is `RequirePlatformOwner`'s own gate on the route this command is
/// posted through - see the handler's own remarks.</param>
/// <param name="ExpiresAt">`22-17`'s own required decision, forced into the wire shape rather than
/// left as an easy-to-forget optional field - see
/// <see cref="EnableModuleForSiteAsOwnerHandler.MaxGrantDuration"/>'s own remarks and this item's
/// report for the full argument. <see langword="null"/> means "does not expire" - a deliberate,
/// legitimate choice for the repair scenario (restoring what a failed payment should have delivered),
/// never a default nobody chose.</param>
/// <remarks>`adr/0150`: carries no <c>ProvisioningSecret</c> - the console's caller (the platform
/// owner, authenticated by <c>RequirePlatformOwner</c>) never holds `adr/0095`'s deployment-wide
/// secret at all. <see cref="EnableModuleForSiteAsOwnerHandler"/> reads it from
/// <see cref="Application.Abstractions.IModuleProvisioningSecretProvider"/> instead - `Ago.Chat.Api`'s
/// own configuration, never the request body.
///
/// <para><b>`23-92`/`adr/0154` extends the identical reasoning to a second field: carries no
/// <c>EntryPoint</c> either.</b> The platform owner can read a module's own address from the cluster
/// exactly as they could already read its provisioning secret; asking them to retype it is a second
/// inconvenience, not a second safeguard - it authorises nothing an entry point could gate.
/// <see cref="EnableModuleForSiteAsOwnerHandler"/> resolves it from
/// <see cref="Application.Abstractions.IModuleEntryPointProvider"/> instead, keyed by
/// <see cref="ModuleKey"/> - never a caller-supplied string, and never a literal this assembly
/// names.</para></remarks>
public sealed record EnableModuleForSiteAsOwner(
    SiteId SiteId, string ModuleKey, IReadOnlyList<string> TriggerWords, string Credential,
    DateTimeOffset? ExpiresAt);
