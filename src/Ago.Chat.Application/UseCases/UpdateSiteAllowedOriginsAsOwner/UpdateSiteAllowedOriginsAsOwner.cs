using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.UpdateSiteAllowedOriginsAsOwner;

/// <summary>
/// `23-48`: the platform owner's own write for a tenant's <see cref="Site.AllowedOrigins"/> - the
/// author's own decision ("the answer", `docs/backlog/23-48-*.md`) is that only the platform owner
/// may ever call this, from `/owner`, and never the tenant themselves. The identical "deliberately
/// separate command/handler for the platform owner's own write surface" shape
/// <see cref="Ago.Chat.Application.UseCases.EnableModuleForSiteAsOwner.EnableModuleForSiteAsOwner"/>'s
/// own remarks describe - except this one has no self-service sibling to be separate *from* at all:
/// there has never been a tenant-facing editor for this field (`23-46`'s own finding), so this is not
/// a second, owner-only copy of an existing write, it is the field's only writer since registration.
/// </summary>
/// <param name="SiteId">The tenant whose allowed origins are being replaced - named directly by the
/// owner, unlike every operator-gated caller in this codebase, where a site is either the caller's own
/// or reached through a resource the caller already owns. The sole reason this is safe is
/// `RequirePlatformOwner`'s own gate on the route this command is posted through - see the handler's
/// own remarks.</param>
/// <param name="AllowedOrigins">The complete replacement list, not a single origin to add or remove -
/// matching how the console screen presents and edits it (one text area, one save), and how
/// <see cref="Site.UpdateAllowedOrigins"/> itself is shaped. Each entry is validated as a real origin
/// before anything is written; an empty list is refused rather than silently locking every visitor
/// out (this item's own Done-when: "a value that is not an origin is refused", extended to "no value
/// at all" for the identical reason).</param>
public sealed record UpdateSiteAllowedOriginsAsOwner(SiteId SiteId, IReadOnlyList<string> AllowedOrigins);
