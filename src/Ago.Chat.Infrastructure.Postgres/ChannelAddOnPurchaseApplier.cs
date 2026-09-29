using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `26-278`: <see cref="IChannelAddOnPurchaseApplier"/>'s own implementation - see that port's own
/// remarks for why this mints a brand-new <see cref="BillingSubscription"/> option row rather than
/// mutating one already loaded, the way <see cref="AdministratorSlotChangeApplier"/> does for the
/// analogous Administrator-slot purchase.
///
/// <para><b>Reuses <c>SubscriptionRenewalApplier</c>'s own grant discipline, deliberately, rather than
/// a bespoke write.</b> <see cref="IModuleQuantityGrantStore.GrantAsync"/> with quantity <c>1</c> is the
/// identical "durable row plus one outbox event, no network call" shape that applier's own remarks
/// already justify at length for a billing-driven channel grant - repeated here rather than referenced,
/// because the two appliers do not share a base class or a common call site to hang a shared helper off
/// without inventing one neither needs for any other reason.</para>
///
/// <para><b><c>CreateOption</c> then <c>MarkSucceeded</c>, both before this row is ever inserted.</b>
/// Unlike a checkout-created base subscription (`Pending` first, `Succeeded` only once ЮKassa's webhook
/// confirms it, sometimes minutes or hours later), this option's own charge already happened
/// synchronously on the handler's own stored payment method by the time this applier is ever called -
/// there is no window in which a caller could observe this row `Pending`, so minting it already
/// `Succeeded` (via two in-memory calls before the first `SaveChangesAsync`) is not a shortcut around
/// any state a caller might need to see, only the honest reflection of what already happened before this
/// method was invoked.</para>
/// </summary>
public sealed class ChannelAddOnPurchaseApplier(
    AgoChatDbContext db, IModuleQuantityGrantStore entitlementGrants, IBillingOptionEntitlementProvider optionEntitlements)
    : IChannelAddOnPurchaseApplier
{
    /// <summary>The identical binary-grant convention <c>SubscriptionRenewalApplier.Granted</c> already
    /// establishes - a channel entitlement has no countable dimension of its own to carry.</summary>
    private const int Granted = 1;

    public async Task ApplyPurchaseAsync(ChannelAddOnPurchaseApplyRequest request, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var option = BillingSubscription.CreateOption(
            request.NewOptionId, request.SiteId, request.YooKassaPaymentId, request.OptionKey, request.Now);
        option.MarkSucceeded(request.PaymentMethodId, request.Now, alignedPeriodEnd: request.AlignedPeriodEnd);
        db.BillingSubscriptions.Add(option);

        // The identical "unreachable in a correctly configured deployment, thrown rather than
        // translated" posture SubscriptionRenewalApplier.ResolveEntitlementOrThrow's own remarks
        // describe - PurchaseChannelAddOnHandler already resolved and validated this exact key from a
        // real ChannelKind, so a deployment reaching here with no BillingOptionEntitlements:channel-*
        // mapping declared is a configuration gap the charge that already succeeded must not be allowed
        // to silently swallow.
        if (optionEntitlements.TryGet(request.OptionKey) is not { } moduleKey)
        {
            throw new InvalidOperationException(
                $"This deployment has not declared an entitlement for billing option '{request.OptionKey.Value}' - set "
                + $"BillingOptionEntitlements:{request.OptionKey.Value} before this channel add-on can be granted.");
        }

        await entitlementGrants.GrantAsync(request.SiteId, moduleKey, Granted, request.Now, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
