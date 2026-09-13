using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.LiftSuspensionAsOwner;

/// <summary>
/// `22-08`: the console's own "unblock" - lifting a suspension early rather than waiting for
/// <see cref="Site.SuspendedUntil"/> to pass on its own. Restores bookings and the widget with no
/// re-provisioning and no new credential (`docs/backlog/22-08-*.md`'s own Done-when): a suspension
/// never touched <see cref="Domain.EnabledModule"/> at all, so lifting it is a write on
/// <see cref="Site"/> alone.
/// </summary>
public sealed record LiftSuspensionAsOwner(SiteId SiteId, string LiftedBy, string Reason);
