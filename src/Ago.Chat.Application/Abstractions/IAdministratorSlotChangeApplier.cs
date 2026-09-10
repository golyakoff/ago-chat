using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `25-41`: the identical "one database transaction, applied only after the prorated charge already
/// succeeded" shape <see cref="ISeatChangeApplier"/> already establishes for seats - restated for a
/// flat Administrator-slot purchase, its own port rather than a widened <see cref="ISeatChangeApplier"/>
/// method, because the two update genuinely different things (<see cref="BillingSubscription.RequestedSeats"/>
/// vs <see cref="BillingSubscription.ExtraAdministratorsPurchased"/>) and nothing about combining them
/// into one interface would remove a parameter either call site actually shares.
/// </summary>
public interface IAdministratorSlotChangeApplier
{
    Task ApplyImmediateIncreaseAsync(AdministratorSlotChangeApplyRequest request, CancellationToken cancellationToken);
}

public sealed record AdministratorSlotChangeApplyRequest(
    BillingSubscriptionId SubscriptionId, SiteId SiteId, int NewExtraAdministratorCount, int AdminExtraPriceVersion,
    DateTimeOffset Now);
