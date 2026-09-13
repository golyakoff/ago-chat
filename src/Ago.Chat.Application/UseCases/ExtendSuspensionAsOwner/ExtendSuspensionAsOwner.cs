using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.ExtendSuspensionAsOwner;

/// <summary>
/// `22-08`: "extend (push <c>suspended_until</c> further out)" - `docs/backlog/22-08-*.md`'s own
/// Scope, verbatim. A deliberately separate command from
/// <c>Ago.Chat.Application.UseCases.SuspendTenantAsOwner.SuspendTenantAsOwner</c> rather than the same
/// one inferring intent from whether the site happens to be suspended already - that handler's own
/// remarks state why.
/// </summary>
/// <param name="AdditionalMinutes">Added to the site's own <em>current</em>
/// <see cref="Site.SuspendedUntil"/>, not to "now" - pushing the deadline further out is exactly what
/// this word means on the console's own list screen, and computing from "now" instead would silently
/// shorten a suspension for a caller who extends it with time still left on the clock.</param>
public sealed record ExtendSuspensionAsOwner(SiteId SiteId, string ExtendedBy, int AdditionalMinutes, string Reason);
