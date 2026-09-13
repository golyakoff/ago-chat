using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `22-08`: the live, uncached read side of <see cref="Domain.Site.SuspendedUntil"/> - the identical
/// "a write decision never reads a cache" shape <see cref="IEnabledModuleReadStore"/> already
/// establishes for an entitlement's own expiry (`CLAUDE.md` rule 8), applied to account-wide
/// suspension. Every caller gating a write or a session mint on this fact goes through here, never
/// through the 5-minute cached <c>SiteConfigDto</c> the widget handshake otherwise reads
/// (<c>Site.SuspendedUntil</c>'s own remarks) - a suspended site must be refused the instant the owner
/// suspends it, not up to five minutes later.
/// </summary>
public interface ISiteSuspensionReadStore
{
    /// <summary>The hot-path gate: is this site suspended right now. <paramref name="now"/> is sourced
    /// from <c>IClock</c> (`CLAUDE.md` rule 11), never the database's own clock - the identical
    /// discipline <see cref="IEnabledModuleReadStore.GetForSiteAsync"/>'s own remarks state for
    /// itself.</summary>
    Task<bool> IsSuspendedAsync(SiteId siteId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Every site whose owner-facing suspension is in effect right now - what
    /// <c>Ago.Chat.Worker.SuspensionLeaseRenewalJob</c> walks on its own fixed cadence to keep the
    /// calendar-side lease alive (`adr/0149` rule 1), and nothing else: a site whose own
    /// <see cref="Site.SuspendedUntil"/> has already passed is not "currently suspended" by this
    /// method's own definition even though nothing has swept its row - the identical "expired means
    /// simply absent from the result, not flagged" shape <see cref="IEnabledModuleReadStore.GetForSiteAsync"/>
    /// already gives an expired module grant.
    /// </summary>
    Task<IReadOnlyList<SiteId>> ListActiveSuspensionsAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// The platform owner's own "who is suspended right now" screen - <see cref="ListActiveSuspensionsAsync"/>'s
    /// sibling, widened with the one fact that method has no use for (the most recent suspension act's
    /// own actor/reason/instant, from <c>site_suspensions</c>) and the display name a bare
    /// <see cref="SiteId"/> cannot carry. A separate method rather than widening the renewal job's own
    /// read: the renewal job runs once every 2.5 minutes for potentially many sites and needs nothing
    /// but an id to republish against, while this read is a low-frequency console poll that needs a
    /// join <see cref="ListActiveSuspensionsAsync"/> would otherwise pay for on every renewal tick.
    /// </summary>
    Task<IReadOnlyList<OwnerSuspensionSummary>> ListForOwnerAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>One row of <see cref="ISiteSuspensionReadStore.ListForOwnerAsync"/> - a currently-suspended
/// site, plus the most recent suspension act recorded against it in <c>site_suspensions</c> (the
/// instant it most recently changed, who did it, and why) - "since when, until when, and what to do
/// about it" restated for the owner's own screen rather than the tenant's.</summary>
public sealed record OwnerSuspensionSummary(
    SiteId SiteId,
    string SiteName,
    DateTimeOffset SuspendedUntil,
    string LastActionBy,
    string LastActionReason,
    DateTimeOffset LastActionAt);
