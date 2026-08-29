using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `13-03`/`decisions/0006`: "upgrades apply immediately" - the one database transaction this half of
/// the mid-cycle policy needs, called only after the prorated charge (`UpgradeSubscriptionSeatsHandler`'s
/// own call, outside any transaction) has already succeeded. Its own port, not folded into
/// <see cref="IBillingSubscriptionRepository"/> or <see cref="ISiteRepository"/> - the identical "its own
/// port because it writes across more than one aggregate" reasoning <see cref="IBillingWebhookApplier"/>
/// already establishes for the analogous first-payment write.
/// </summary>
public interface ISeatChangeApplier
{
    Task ApplyImmediateIncreaseAsync(SeatChangeApplyRequest request, CancellationToken cancellationToken);
}

public sealed record SeatChangeApplyRequest(
    BillingSubscriptionId SubscriptionId, SiteId SiteId, int NewSeatCount, string NewTier, DateTimeOffset Now);
