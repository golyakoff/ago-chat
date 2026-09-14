using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>`25-84`: <see cref="IDownloadOverageReadStore"/> in memory. Seeded with whole charge rows
/// rather than with pre-computed sums, so the fake derives its two answers the same way the real Dapper
/// store does - a fake that took a settled total directly would let a handler test pass while the real
/// <c>SUM</c> disagreed about which rows count (only <c>Succeeded</c> ones do, and only
/// <c>Checkout</c>-sourced ones set <c>HasPaidCheckout</c>).</summary>
public sealed class FakeDownloadOverageReadStore : IDownloadOverageReadStore
{
    private readonly List<SeededCharge> _charges = [];
    private readonly Dictionary<(Guid SiteId, DateOnly PeriodMonth), long> _egressBytes = [];

    public void SeedCharge(
        SiteId siteId, DateOnly periodMonth, long bytesOver, decimal amountRub,
        DownloadOverageChargeSource source = DownloadOverageChargeSource.Checkout,
        DownloadOverageChargeStatus status = DownloadOverageChargeStatus.Succeeded) =>
        _charges.Add(new SeededCharge(siteId.Value, periodMonth, bytesOver, amountRub, source, status));

    /// <summary>Only <see cref="GetOutstandingAsync"/> needs this - the real store reads it by joining
    /// <c>site_attachment_egress</c>, which this fake has no access to.</summary>
    public void SeedEgress(SiteId siteId, DateOnly periodMonth, long bytesOut) =>
        _egressBytes[(siteId.Value, periodMonth)] = bytesOut;

    public Task<DownloadOverageSettlement> GetSettlementAsync(
        SiteId siteId, DateOnly periodMonth, CancellationToken cancellationToken)
    {
        var settled = _charges
            .Where(c => c.SiteId == siteId.Value
                && c.PeriodMonth == periodMonth
                && c.Status == DownloadOverageChargeStatus.Succeeded)
            .ToList();

        return Task.FromResult(new DownloadOverageSettlement(
            settled.Sum(c => c.BytesOver),
            settled.Sum(c => c.AmountRub),
            settled.Any(c => c.Source == DownloadOverageChargeSource.Checkout)));
    }

    public Task<IReadOnlyList<DownloadOverageOutstanding>> GetOutstandingAsync(
        SiteId siteId, long hardThresholdBytes, DateOnly upToPeriodMonth, CancellationToken cancellationToken)
    {
        var result = new List<DownloadOverageOutstanding>();

        foreach (var ((egressSiteId, periodMonth), bytesOut) in _egressBytes.OrderBy(e => e.Key.PeriodMonth))
        {
            if (egressSiteId != siteId.Value || periodMonth > upToPeriodMonth)
            {
                continue;
            }

            var settled = _charges
                .Where(c => c.SiteId == siteId.Value
                    && c.PeriodMonth == periodMonth
                    && c.Status == DownloadOverageChargeStatus.Succeeded)
                .ToList();

            var outstanding = bytesOut - hardThresholdBytes - settled.Sum(c => c.BytesOver);
            if (outstanding > 0)
            {
                result.Add(new DownloadOverageOutstanding(
                    periodMonth, outstanding, settled.Any(c => c.Source == DownloadOverageChargeSource.Checkout)));
            }
        }

        return Task.FromResult<IReadOnlyList<DownloadOverageOutstanding>>(result);
    }

    private sealed record SeededCharge(
        Guid SiteId,
        DateOnly PeriodMonth,
        long BytesOver,
        decimal AmountRub,
        DownloadOverageChargeSource Source,
        DownloadOverageChargeStatus Status);
}
