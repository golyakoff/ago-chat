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

    // `25-84`: the download-overage lines the handler decided this renewal's own charge carried -
    // recorded so a test can assert on what was swept, not merely that a renewal happened.
    public List<IReadOnlyList<DownloadOverageInvoiceLine>> RenewedWithOverageLines { get; } = [];

    public Task ApplyRenewalSuccessAsync(
        BillingSubscriptionId id, DateTimeOffset now, int baseSeatPriceVersion, int extraSeatPriceVersion,
        IReadOnlyList<DownloadOverageInvoiceLine> overageSettlements,
        CancellationToken cancellationToken)
    {
        RenewedSuccessfully.Add(id);
        RenewedWithPriceVersions.Add((baseSeatPriceVersion, extraSeatPriceVersion));
        RenewedWithOverageLines.Add(overageSettlements);
        return Task.CompletedTask;
    }

    public Task ApplyRenewalFailureAsync(BillingSubscriptionId id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        RenewalFailures.Add(id);
        return Task.CompletedTask;
    }
}
