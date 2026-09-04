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
public sealed record EnableModuleForSiteAsOwner(
    SiteId SiteId, string ModuleKey, IReadOnlyList<string> TriggerWords, string EntryPoint, string Credential,
    string ProvisioningSecret, DateTimeOffset? ExpiresAt);
