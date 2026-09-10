using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `13-03`: <see cref="ISubscriptionRenewalApplier"/>'s own implementation - each method opens its own
/// transaction, reloads the row fresh inside it (the port's own remarks on why), mutates, and - only
/// for a branch that changes what the site is entitled to - stages the same `SiteSettingsChanged`
/// outbox row `BillingWebhookApplier` already produces for the analogous first-payment write, reusing
/// <see cref="SiteSubscriptionActivatedMapper"/> rather than inventing a second cache-invalidation
/// shape for what is, from a cache's own point of view, the identical fact ("this site's tier/seat
/// limit changed").
///
/// <para><b>`23-86`/`adr/0159`: an option row's own renewal/lapse grants or revokes its entitlement in
/// this identical transaction</b> - "same transaction as the existing applier writes, published through
/// the outbox" (this item's own brief). <see cref="IModuleQuantityGrantStore"/> is the write chosen for
/// this, not <see cref="Domain.EnabledModule"/>: an <see cref="Domain.EnabledModule"/> grant needs a
/// real entry point, a real credential, and - for a genuinely externally-routed module -
/// <see cref="IModuleRegistrationGateway.RegisterAsync"/>'s own synchronous confirmation call before the
/// row is trustworthy (`EnableModuleForSiteAsOwnerHandler`'s own remarks), and none of that belongs
/// inside a background job's own database transaction that also just charged a real card - a network
/// call inside a financial transaction is exactly the failure mode CLAUDE.md rule 3 exists to keep out.
/// <see cref="IModuleQuantityGrantStore.GrantAsync"/> is already the "durable row plus one outbox event,
/// no network call, one caller-provided number" shape `22-07` built for the calendar's own add-on quota
/// - reused here with <see cref="ModuleQuantityGrant.Quantity"/> collapsed to the binary case (`1`
/// granted, `0` revoked) rather than a real count, since a channel or an AI addition has no countable
/// dimension of its own to carry. <b>Accepted judgement, recorded on
/// `docs/architecture/entitlements-and-subscriptions.md`</b>: a billing grant writes a quantity grant,
/// not an <see cref="Domain.EnabledModule"/> row, because that row needs an entry point, a credential
/// and a synchronous registration call that do not belong inside a job's own transaction seconds after
/// charging a card. That page also says plainly what this means for the two real
/// registration-requiring modules: <b>this mechanism is not sufficient for `calendar`/`faq`</b> - a
/// deployment must not point <see cref="IBillingOptionEntitlementProvider"/> at either until a wider
/// mechanism exists; widening it is an open question, not a decision this item made.</para>
///
/// <para><b>Only an option row's own transitions reach this</b> - a base row's lapse still only
/// downgrades <c>Site.Tier</c>/<c>Site.SeatLimit</c>, exactly as before this item, and never touches any
/// option's own entitlement. <b>`adr/0160`</b> settles what happens to an option when the *base* lapses:
/// a paid option is not cancelled by a base lapse - it runs on its own subscription and its own money.
/// This applier's own shape already matches that decision by construction (the branch below never
/// reaches an option's entitlement from a base row's own lapse), proven by
/// <c>ApplyLapseAsync_ForTheBaseSubscription_NeverTouchesAnOptionsOwnEntitlement</c>.</para>
/// </summary>
public sealed class SubscriptionRenewalApplier(
    AgoChatDbContext db, IOutboxWriter outbox, IIdGenerator idGenerator,
    IModuleQuantityGrantStore entitlementGrants, IBillingOptionEntitlementProvider optionEntitlements)
    : ISubscriptionRenewalApplier
{
    /// <summary><see cref="ModuleQuantityGrant.Quantity"/> used to mean "this option's entitlement is
    /// granted" - see this type's own remarks for why a snapshot quantity, collapsed to a binary case,
    /// is what an option's entitlement is written as.</summary>
    private const int Granted = 1;

    /// <summary>The counterpart of <see cref="Granted"/> - a revoke is <see cref="Granted"/>'s own
    /// snapshot set back to zero, not a delete, the identical "set the current value, not a delta"
    /// idempotence <see cref="ModuleQuantityGrant"/>'s own remarks describe.</summary>
    private const int Revoked = 0;

    public async Task ApplyLapseAsync(BillingSubscriptionId id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var subscription = await LoadOrThrowAsync(id, cancellationToken);
        subscription.MarkLapsed();

        if (subscription.OptionKey is { } optionKey)
        {
            // This row's own OptionKey decides the branch - a base row (OptionKey null) always takes
            // the `else` below, unchanged from before this item. `adr/0160`'s own reverse direction:
            // nothing here ever revokes an option because the *base* lapsed - only this row's own lapse
            // reaches this branch, matching `adr/0160`'s "a paid option is not cancelled by a base
            // lapse".
            await RevokeEntitlementAsync(subscription.SiteId, optionKey, now, cancellationToken);
        }
        else
        {
            var site = await LoadSiteOrThrowAsync(subscription.SiteId, cancellationToken);
            site.ActivateSubscription("free", 1, now);
            StageSiteSettingsChanged(site);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ApplyRenewalSuccessAsync(
        BillingSubscriptionId id, DateTimeOffset now, int baseSeatPriceVersion, int extraSeatPriceVersion,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var subscription = await LoadOrThrowAsync(id, cancellationToken);

        if (subscription.OptionKey is { } optionKey)
        {
            // `25-43`: meaningless for an option row - priced flat, not by seats, so there is no
            // seat-pricing key for either to name (BillingSubscription.CreateOption's own convention).
            subscription.RecordRenewalSuccess(now, paymentMethodId: null, baseSeatPriceVersion: 0, extraSeatPriceVersion: 0);
            await GrantEntitlementAsync(subscription.SiteId, optionKey, now, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        var seatsBefore = subscription.RequestedSeats;
        var tierBefore = subscription.Tier;

        subscription.RecordRenewalSuccess(now, paymentMethodId: null, baseSeatPriceVersion, extraSeatPriceVersion);

        // A pending deferred downgrade only ever changes RequestedSeats/Tier inside RecordRenewalSuccess
        // itself - comparing before/after is this applier's own way of learning "did that happen" without
        // RecordRenewalSuccess needing to hand back a second return value nothing else would use.
        if (subscription.RequestedSeats != seatsBefore || subscription.Tier != tierBefore)
        {
            var site = await LoadSiteOrThrowAsync(subscription.SiteId, cancellationToken);
            site.ActivateSubscription(subscription.Tier, subscription.RequestedSeats, now);
            StageSiteSettingsChanged(site);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Resolves <paramref name="optionKey"/> to the <see cref="ModuleKey"/> it grants and stages
    /// the same-transaction, idempotent snapshot write - see this type's own remarks for the whole
    /// mechanism. A missing mapping is thrown, not swallowed: the identical "unreachable in a correctly
    /// configured deployment, thrown rather than translated" posture <see cref="LoadOrThrowAsync"/>'s own
    /// remarks describe, since there is no caller here to hand a `Result` failure back to - only the
    /// job's own per-candidate `try`/`catch`, which logs and retries next tick, matching
    /// `ProcessSubscriptionRenewalHandler`'s own remarks on why a thrown exception is the right shape
    /// for a fault this deep in the pipeline. A configuration gap surfacing as a loud, repeating failure
    /// is the correct failure mode for it - the alternative, granting nothing and returning normally,
    /// would make a paid option silently do nothing with no signal anywhere.</summary>
    private async Task GrantEntitlementAsync(
        SiteId siteId, BillingOptionKey optionKey, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var moduleKey = ResolveEntitlementOrThrow(optionKey);
        await entitlementGrants.GrantAsync(siteId, moduleKey, Granted, now, cancellationToken);
    }

    /// <summary>The revoke half of <see cref="GrantEntitlementAsync"/> - identical resolution, the
    /// identical same-transaction write, quantity set back to <see cref="Revoked"/> rather than the row
    /// deleted, the same "a snapshot, re-set rather than removed" shape <see cref="ModuleQuantityGrant"/>'s
    /// own remarks describe.</summary>
    private async Task RevokeEntitlementAsync(
        SiteId siteId, BillingOptionKey optionKey, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var moduleKey = ResolveEntitlementOrThrow(optionKey);
        await entitlementGrants.GrantAsync(siteId, moduleKey, Revoked, now, cancellationToken);
    }

    private ModuleKey ResolveEntitlementOrThrow(BillingOptionKey optionKey)
    {
        if (optionEntitlements.TryGet(optionKey) is not { } moduleKey)
        {
            // Deliberately a plain string, not a reference to
            // Ago.Chat.Infrastructure.Modules.ConfiguredBillingOptionEntitlementProvider.SectionName -
            // Infrastructure.Postgres has no reason to take a project reference on a sibling
            // Infrastructure adapter (a DB access shape referencing an HTTP-module-registry shape) for
            // one constant string in an exception message; IBillingOptionEntitlementProvider's own
            // Application-layer remarks are the single source of truth for the section name's meaning.
            throw new InvalidOperationException(
                $"This deployment has not declared an entitlement for billing option '{optionKey.Value}' - set "
                + $"BillingOptionEntitlements:{optionKey.Value} before this option can be granted or revoked.");
        }

        return moduleKey;
    }

    public async Task ApplyRenewalFailureAsync(BillingSubscriptionId id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var subscription = await LoadOrThrowAsync(id, cancellationToken);
        if (subscription.Status == BillingSubscriptionStatus.PastDue)
        {
            subscription.RecordRenewalRetryFailure(now);
        }
        else
        {
            subscription.RecordRenewalFailure(now);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<BillingSubscription> LoadOrThrowAsync(BillingSubscriptionId id, CancellationToken cancellationToken)
    {
        var subscription = await db.BillingSubscriptions.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (subscription is null)
        {
            // The candidate list this applier's own caller (ProcessSubscriptionRenewalHandler) reads
            // from is a moment-old snapshot of the same table - a row it names must still exist by the
            // time this call runs, since nothing in this codebase ever deletes a billing_subscriptions
            // row. Thrown, not translated into a result case, the same "unreachable, thrown rather than
            // translated" shape BillingWebhookApplier's own missing-site guard describes.
            throw new InvalidOperationException($"Billing subscription {id.Value} was not found while applying a renewal outcome.");
        }

        return subscription;
    }

    private async Task<Site> LoadSiteOrThrowAsync(SiteId siteId, CancellationToken cancellationToken)
    {
        var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == siteId, cancellationToken);
        if (site is null)
        {
            throw new InvalidOperationException(
                $"Site {siteId.Value} was not found while applying a subscription renewal outcome - a foreign key should have prevented this.");
        }

        return site;
    }

    private void StageSiteSettingsChanged(Site site)
    {
        var activated = site.DomainEvents.OfType<SiteSubscriptionActivated>().Single();
        outbox.Enqueue(SiteSubscriptionActivatedMapper.ToEnvelope(activated, idGenerator));
        site.ClearDomainEvents();
    }
}
