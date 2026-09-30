using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.SetModuleTriggerWordsForSite;

/// <summary>
/// `26-320`: the tenant admin's own edit of an already-enabled module's trigger words - the write half
/// `26-316` left out. `26-316` reinstated the tenant's own enable/disable
/// (<see cref="EnableModuleForSite.EnableModuleForSite"/>) and seeded a default trigger word at enable
/// time, but gave no way to change it afterward; a tenant admin who wanted to add `/booking` beside
/// `/записаться`, or replace it, still needed a platform owner to do it by hand
/// (<see cref="EnableModuleForSiteAsOwner.EnableModuleForSiteAsOwner"/>, the only path that ever set them).
/// This command closes that gap for the caller's own site.
///
/// <para><b>Carries the caller's own <see cref="RequestedBy"/> and a route-supplied <see cref="SiteId"/>,
/// never a body-supplied tenant</b> - the identical shape <see cref="EnableModuleForSite.EnableModuleForSite"/>
/// carries, and for the identical reason: the handler gates the pair through
/// <see cref="Application.Abstractions.IPermissionChecker"/> on <see cref="Permission.SiteConfigure"/>, so
/// this only ever edits a module for a site the caller already administers - not a `RequirePlatformOwner`
/// policy (that is the owner grant's authorisation, and re-pointing an owner grant's triggers stays with
/// the owner). This is the whole difference in authorisation from the owner path, exactly as it is for
/// enable/disable.</para>
/// </summary>
/// <param name="TriggerWords">The complete replacement set - this command <em>replaces</em> the module's
/// trigger words, it does not merge into them, so the caller sends the whole list every time (the same
/// shape the console's own form edits). Opaque to <c>Ago.Chat.*</c> (<see cref="Domain.ModuleKey"/>'s own
/// remarks): the console knows what a "calendar" is and what phrase should open it, this codebase never
/// does. The widget renders its booking chip from the first of these (`25-131`).</param>
public sealed record SetModuleTriggerWordsForSite(
    OperatorId RequestedBy, SiteId SiteId, string ModuleKey, IReadOnlyList<string> TriggerWords);
