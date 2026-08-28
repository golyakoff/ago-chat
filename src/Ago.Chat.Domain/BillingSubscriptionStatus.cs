namespace Ago.Chat.Domain;

/// <summary>
/// `13-02`: the lifecycle of one checkout attempt, tracked on <see cref="BillingSubscription"/> from
/// the moment ЮKassa hands back a payment id (<see cref="Pending"/>) to whichever terminal outcome its
/// webhook eventually confirms. Only two transitions exist, both owned by <see cref="BillingSubscription"/>
/// itself (<see cref="BillingSubscription.MarkSucceeded"/>/<see cref="BillingSubscription.MarkFailed"/>) -
/// `Pending -> Succeeded` and `Pending -> Failed`, never a transition back out of a terminal state, and
/// never a third outcome: this item's own Scope stops at "first payment succeeds, tier updates" - a
/// renewal, refund or cancellation lifecycle is `13-03`'s, not a state this enum needs to represent yet.
/// </summary>
public enum BillingSubscriptionStatus
{
    Pending,
    Succeeded,
    Failed,
}
