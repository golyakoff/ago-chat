using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.PurchaseAdministratorSlot;

/// <summary>
/// `25-41`: the purchase path `ChangeSubscriptionSeatsHandler` already establishes for seats, mirrored
/// for a flat Administrator-slot add-on - a single new endpoint
/// (<c>POST .../billing/subscriptions/{id}/administrators</c>), only ever an immediate, charged
/// increase. Unlike seats, there is no downgrade branch this command's own handler needs to choose
/// between: this codebase has no self-service way to buy fewer Administrator slots at all
/// (<see cref="BillingSubscription.ApplyAdministratorPurchase"/>'s own remarks) - the only way this
/// count ever goes down is the automatic demotion a lapse or a downgrade triggers
/// (<c>IAdministratorLimitEnforcer</c>), never a request through this command.
/// </summary>
public sealed record PurchaseAdministratorSlot(
    OperatorId RequestedBy, SiteId SiteId, BillingSubscriptionId SubscriptionId, int RequestedExtraAdministrators);

/// <summary>Charged immediately, applied immediately - <paramref name="ProratedAmountRub"/> is what was
/// actually charged, computed against the subscription's own real <see cref="BillingSubscription.CurrentPeriodEnd"/>,
/// the identical proration <c>ChangeSubscriptionSeatsResult.Upgraded</c> already reports for the
/// analogous seat purchase.</summary>
public sealed record PurchaseAdministratorSlotResult(decimal ProratedAmountRub, int NewExtraAdministratorCount);
