using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `23-73`: the account-inactivity watchdog's own reset - stamps a site's "last operator activity"
/// timestamp forward and, in the same write, clears any pending inactivity-warning flag, because a
/// fresh reset starts a fresh three-month cycle and a warning sent against the *previous* cycle would
/// otherwise survive into this one and suppress a real future warning.
///
/// <para><b>Its own port, not a method on <see cref="ISiteRepository"/>.</b> The identical reasoning
/// <see cref="IErasureRequestRepository"/>'s own remarks give for <c>erasure_requested_at</c>: this
/// column has exactly two legitimate writers (an operator's own outbound message, landing inside
/// <c>Ago.Chat.Infrastructure.Postgres.Pipeline.MessageBatchWriter</c>'s already-open transaction, and
/// a real authenticated request reaching <c>Ago.Chat.Api.Auth.OperatorIdentityClaimsTransformation</c>)
/// and one reader (<c>Ago.Chat.Worker.InactivityWatchdogJob</c>'s own sweep query) - never through
/// <see cref="Site"/>'s own load-mutate-<c>SaveChangesAsync</c> path, and nothing about <see cref="Site"/>'s
/// own behaviour needs to reason about "when did an operator last act" at all. Routing a single-column,
/// high-frequency stamp through the full aggregate would mean loading and re-saving a site's entire
/// `WidgetConfig`/`OfflineAutoReply` on every reply an operator ever sends - the same "reaches a row
/// without going through its aggregate" shape <see cref="IErasureRequestRepository"/>/
/// <see cref="IDemoTenantRepository"/> already established for exactly this reason.</para>
///
/// <para><b>Idempotent and self-throttling, not merely idempotent.</b> A call before
/// <c>SiteActivityWatchdogOptions.MinTouchInterval</c> has elapsed since the last recorded touch is a
/// no-op - deliberately, since <see cref="Ago.Chat.Api.Auth.OperatorIdentityClaimsTransformation"/>
/// calls this on every authenticated request, and a plain unconditional <c>UPDATE</c> on that path would
/// turn an ordinary console page load into a write on the <c>sites</c> table. See
/// <c>SiteActivityWatchdogQuery</c>'s own remarks for why this is expressed as one conditional
/// <c>UPDATE</c> rather than a read-then-write, and <c>OperatorIdentityClaimsTransformation</c>'s own
/// remarks for why a cache-backed debounce was considered and deliberately not built yet.</para>
/// </summary>
public interface ISiteActivityWatchdog
{
    Task TouchAsync(SiteId siteId, DateTimeOffset at, CancellationToken cancellationToken);
}
