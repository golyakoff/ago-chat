using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.ChangeSubscriptionSeats;

/// <summary>
/// `13-03`/`decisions/0006`'s asymmetric mid-cycle policy - one command, two branches decided entirely
/// by comparing <paramref name="RequestedSeats"/> against the subscription's current seat count (an
/// increase charges immediately and applies now; a decrease is deferred to the next renewal, no
/// proration). A single new endpoint (<c>POST .../billing/subscriptions/{id}/seats</c>), not a second
/// code path grafted onto `13-02`'s own checkout-session endpoint - this item's own implementer's-call:
/// "change an existing subscription" is a different enough operation (no ЮKassa redirect, no new
/// `billing_subscriptions` row) that sharing the checkout endpoint's own route would mean branching on
/// "does `{id}` already exist" inside a handler whose entire other half assumes it never does.
/// </summary>
public sealed record ChangeSubscriptionSeats(
    OperatorId RequestedBy, SiteId SiteId, BillingSubscriptionId SubscriptionId, int RequestedSeats);

public abstract record ChangeSubscriptionSeatsResult
{
    private ChangeSubscriptionSeatsResult()
    {
    }

    /// <summary>Charged immediately, applied immediately - <paramref name="ProratedAmountRub"/> is what
    /// was actually charged, computed against the subscription's own real
    /// <see cref="BillingSubscription.CurrentPeriodEnd"/>, not a fixed figure.</summary>
    public sealed record Upgraded(decimal ProratedAmountRub, string NewTier, int NewSeatCount) : ChangeSubscriptionSeatsResult;

    /// <summary>Recorded, not applied - takes effect at the subscription's own next renewal, no charge
    /// made now.</summary>
    public sealed record DowngradeScheduled(string NewTier, int NewSeatCount) : ChangeSubscriptionSeatsResult;
}
