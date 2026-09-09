using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GrantModuleQuantityAsOwner;

/// <summary>
/// `23-66`: the platform owner's own half of `22-07`'s crossing - the write that had a store
/// (<see cref="IModuleQuantityGrantStore"/>) and a consumer (`ago-calendar`'s own
/// `ModuleQuantityGrantedConsumer`) but no way to be called at all. Never asks the module anything -
/// rule 8 forbids the calendar (or any module) being asked at write time whether it may create the
/// next worker, so this handler's whole job, like its tenant-facing sibling's, is making the granted
/// number durable on this side and telling the module it changed.
///
/// <para><b>`23-88`: when <see cref="GrantModuleQuantityAsOwner.ExpectedAffectedCount"/> is supplied,
/// this handler refuses rather than apply a change against a consequence nobody actually saw.</b>
/// The check is entirely against <see cref="IModuleQuantityImpactPreviewStore"/>'s own stored copy of
/// the module's last answer - never a live call to the module (this handler's own remarks above,
/// unchanged by this item: rule 8 still forbids that at this write). That is a real, named
/// limitation, not the item's own full intent: the module's own answer could itself be a few seconds
/// stale by the time this call runs, and this handler cannot see past that without the live,
/// synchronous call the whole mechanism exists to avoid. The unconditional safety net stays where it
/// always was - the module's own live recompute at grant-application time (`adr/0125`'s own
/// lock-and-count, for the calendar's own worker quota) - so no *wrong* deactivation can ever result
/// from either handler here being wrong about a stale number. What this check actually protects is
/// narrower and still real: whether the owner's own confirmation was made against the answer that
/// was actually shown, not a number that changed under them or was never answered at all.</para>
///
/// <para>Refuses on any of: no preview row for this site/module at all; the stored row's own
/// <c>RequestedQuantity</c> naming a different candidate than <see cref="GrantModuleQuantityAsOwner.Quantity"/>
/// (the owner is confirming a number they never actually previewed); the module never having
/// answered; or the stored <c>AffectedCount</c> disagreeing with
/// <see cref="GrantModuleQuantityAsOwner.ExpectedAffectedCount"/> (a newer answer superseded the one
/// the owner saw). All four collapse to the identical <see cref="ConversationErrors.ModuleQuantityImpactStale"/>
/// - the remedy is the same regardless: ask again, look at the fresh answer, confirm again.</para>
/// </summary>
public sealed class GrantModuleQuantityAsOwnerHandler(
    IModuleQuantityGrantStore grants, IModuleQuantityImpactPreviewStore previews, IClock clock)
{
    public async Task<Result> HandleAsync(GrantModuleQuantityAsOwner command, CancellationToken cancellationToken)
    {
        ModuleKey moduleKey;
        try
        {
            moduleKey = new ModuleKey(command.ModuleKey);
        }
        catch (ArgumentException ex)
        {
            return ConversationErrors.ModuleInvalid(ex.Message);
        }

        if (command.Quantity < 0)
        {
            return ConversationErrors.ModuleInvalid("A granted quantity cannot be negative.");
        }

        if (command.ExpectedAffectedCount is { } expectedAffectedCount)
        {
            var preview = await previews.TryGetAsync(command.SiteId, moduleKey, cancellationToken);
            if (preview is null
                || preview.RequestedQuantity != command.Quantity
                || preview.AnsweredAt is null
                || preview.AffectedCount != expectedAffectedCount)
            {
                return ConversationErrors.ModuleQuantityImpactStale(
                    "The impact shown for this quantity has changed or was never answered - preview it again before confirming.");
            }
        }

        await grants.GrantAsync(command.SiteId, moduleKey, command.Quantity, clock.UtcNow, cancellationToken);
        return Result.Success();
    }
}
