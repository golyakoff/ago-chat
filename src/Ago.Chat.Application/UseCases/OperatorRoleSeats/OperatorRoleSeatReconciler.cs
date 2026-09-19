using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.OperatorRoleSeats;

/// <summary>
/// `25-170`: "one reconciliation procedure, parameterized the same way, replaces
/// `AdministratorLimitEnforcer` entirely" (this item's own design). For a given site and role: count
/// current `HoldsSeat = true` holders of that role, and if over that role's own `Site` limit, flip
/// `HoldsSeat = false` on the excess ordered by `GrantedAt` descending (most recently granted loses
/// first) until back at or under the limit. Applied to the Operator role and the Admin role identically -
/// unlike `AdministratorLimitEnforcer`'s own retired `DemoteExcessAdministratorsAsync`, an excess holder
/// is <em>disabled</em>, never moved sideways into a different role: the author's own decision (this
/// item's design conversation) is that someone over the Admin-role limit gets the identical outcome
/// someone over the Operator-role limit already gets, not shuffled into a role that might itself now
/// also be full.
///
/// <para><b>Callers: the new `Ago.Chat.Worker` watchdog (every site, every minute) and
/// `SubscriptionRenewalApplier`'s own transaction, as a same-effect fast path for the instant case.</b>
/// The watchdog is what actually guarantees a downgrade is caught within a bounded time no matter how a
/// site's <see cref="Site.AdminLimit"/> came to drop; the renewal applier's own call is optional latency
/// only, kept because the exact call site was already identified and costs nothing extra to keep
/// instant for the most common trigger.</para>
///
/// <para><b>Takes an already-loaded <see cref="Site"/>, not a <see cref="SiteId"/>.</b> Deliberately, and
/// found necessary rather than assumed while wiring `SubscriptionRenewalApplier`'s own call: that method
/// mutates its own tracked `Site` in memory (<see cref="Site.ActivateSubscription"/>) and calls this
/// procedure <em>before</em> its own `SaveChangesAsync` - a fresh `ISiteRepository.GetByIdAsync` read at
/// that point, inside the same transaction, would still see the <em>previous</em> `AdminLimit`, since
/// nothing has flushed yet. Taking the caller's own already-mutated aggregate directly sidesteps that
/// entirely; the watchdog, which has no in-memory dirty `Site` of its own, simply loads one fresh and
/// passes it the identical way.</para>
///
/// <para><b>No <see cref="Ago.Platform.Abstractions.IClock"/> dependency.</b> The item's own design note
/// asks for this procedure to be `IClock`-driven; checked directly against what this procedure actually
/// does, there is no timestamp it needs to produce - `GrantedAt` is written only when a role is granted
/// (`ChangeOperatorRoleHandler`/`OperatorInviteRedemptionRepository`/`SiteRegistrationRepository`), never
/// by disabling a seat, so a "now" here would have no write to attach to. The caller that does need one -
/// the new watchdog job, for its own per-tick logging - reads its own <c>IClock.UtcNow</c> instead; this
/// class stays clock-free because it genuinely has nothing time-dependent to decide.</para>
/// </summary>
public sealed class OperatorRoleSeatReconciler(IOperatorRoleRepository operatorRoles)
{
    /// <summary>Returns how many holders were disabled - <c>0</c> when already at or under the limit,
    /// the identical "let the callee decide there is nothing to do" no-op shape
    /// `AdministratorLimitEnforcer`'s own pre-`25-170` remarks described for itself.</summary>
    public async Task<int> ReconcileAsync(Site site, string roleName, CancellationToken cancellationToken)
    {
        var limit = RoleSeatLimits.LimitFor(roleName, site);

        // The identical row-locked read the capacity-check procedure uses - a concurrent capacity
        // check or seat toggle on this same site and role serializes behind this same lock rather than
        // racing this sweep.
        var holderIds = await operatorRoles.LockAndGetHeldSeatHolderIdsAsync(site.Id, roleName, cancellationToken);
        var excessCount = holderIds.Count - limit;
        if (excessCount <= 0)
        {
            return 0;
        }

        // Already ordered most-recently-granted-first (IOperatorRoleRepository's own contract) - take
        // exactly the excess off the front.
        foreach (var operatorId in holderIds.Take(excessCount))
        {
            await operatorRoles.SetHoldsSeatAsync(operatorId, site.Id, roleName, holdsSeat: false, cancellationToken);
        }

        return excessCount;
    }
}
