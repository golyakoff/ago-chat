using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeSubscriptionRenewalApplier : ISubscriptionRenewalApplier
{
    public List<BillingSubscriptionId> Lapsed { get; } = [];

    public List<BillingSubscriptionId> RenewedSuccessfully { get; } = [];

    public List<BillingSubscriptionId> RenewalFailures { get; } = [];

    // `25-43`: the price version numbers ProcessSubscriptionRenewalHandler actually passed through -
    // recorded so a test can assert the renewal applied the version it read at charge time, not just
    // that a renewal happened at all.
    public List<(int BaseSeatPriceVersion, int ExtraSeatPriceVersion)> RenewedWithPriceVersions { get; } = [];

    public Task ApplyLapseAsync(BillingSubscriptionId id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Lapsed.Add(id);
        return Task.CompletedTask;
    }

    public Task ApplyRenewalSuccessAsync(
        BillingSubscriptionId id, DateTimeOffset now, int baseSeatPriceVersion, int extraSeatPriceVersion,
        CancellationToken cancellationToken)
    {
        RenewedSuccessfully.Add(id);
        RenewedWithPriceVersions.Add((baseSeatPriceVersion, extraSeatPriceVersion));
        return Task.CompletedTask;
    }

    public Task ApplyRenewalFailureAsync(BillingSubscriptionId id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        RenewalFailures.Add(id);
        return Task.CompletedTask;
    }
}
