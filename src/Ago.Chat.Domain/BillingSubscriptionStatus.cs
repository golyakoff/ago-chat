namespace Ago.Chat.Domain;

/// <summary>
/// `13-02`: the lifecycle of one checkout attempt, tracked on <see cref="BillingSubscription"/> from
/// the moment ЮKassa hands back a payment id (<see cref="Pending"/>) to whichever terminal outcome its
/// webhook eventually confirms. `13-02`'s own two transitions - `Pending -> Succeeded` and
/// `Pending -> Failed` - are unchanged; `13-03` extends the same row past that first payment rather
/// than starting a second one (<see cref="BillingSubscription"/>'s own remarks on why it was already
/// named for what it becomes), so two more states and their own transitions arrive here rather than in
/// a new type:
///
/// <list type="bullet">
/// <item><see cref="Succeeded"/> -&gt; <see cref="PastDue"/>: a recurring re-charge failed
/// (<see cref="BillingSubscription.RecordRenewalFailure"/>). `Site.Tier`/`Site.SeatLimit` are
/// untouched - `decisions/0006`'s "full access retained" - only this row's own status moves.</item>
/// <item><see cref="PastDue"/> -&gt; <see cref="Succeeded"/>: a retry inside the 7-day window
/// succeeded (<see cref="BillingSubscription.RecordRenewalSuccess"/>). Entitlements were never
/// interrupted, so there is nothing for the site to restore.</item>
/// <item><see cref="PastDue"/> -&gt; <see cref="Lapsed"/>: the 7-day retry window closed with no
/// successful retry (<see cref="BillingSubscription.MarkLapsed"/>) - the same write path that downgrades
/// the site to `tier='free'`/`seat_limit=1` also applies here.</item>
/// <item><see cref="Succeeded"/> -&gt; <see cref="Lapsed"/>: a cancelled subscription
/// (<see cref="BillingSubscription.RequestCancellation"/>) reached its own paid-through
/// `current_period_end` with no re-charge ever attempted - the identical terminal state a lapsed
/// `PastDue` row reaches, because from the site's own point of view "ran out of retries" and "chose not
/// to renew" both end in exactly the same place: free tier, no refund, everything else intact. One
/// terminal state for both, rather than two that would need identical downstream handling everywhere
/// they are read.</item>
/// </list>
///
/// Never a transition back out of <see cref="Lapsed"/> or <see cref="Failed"/> - a site that wants
/// paid access again goes through `13-02`'s own checkout, a fresh <see cref="Pending"/> row (this
/// item's own Out of scope: no "reactivate this exact subscription" path).
/// </summary>
public enum BillingSubscriptionStatus
{
    Pending,
    Succeeded,
    Failed,

    /// <summary>`13-03`: a recurring re-charge failed; retries run daily for up to 7 days
    /// (<see cref="BillingSubscription.PastDueSince"/>) while the site's entitlements stay exactly as
    /// they were (`decisions/0006`).</summary>
    PastDue,

    /// <summary>`13-03`: this subscription no longer entitles anything - reached either by exhausting
    /// the <see cref="PastDue"/> retry window or by an explicit cancellation running out its paid-through
    /// period. The site has already been (or is being, in the same transaction) downgraded to
    /// `tier='free'`/`seat_limit=1`.</summary>
    Lapsed,
}
