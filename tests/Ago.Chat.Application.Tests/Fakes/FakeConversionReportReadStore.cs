using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Records the exact bound `GetConversionReportForSiteHandler` resolved and hands back
/// whatever this fake was seeded with - the same shape `FakeOperatorAnalyticsReadStore` already
/// establishes for `18-08`'s analogous handler.</summary>
public sealed class FakeConversionReportReadStore : IConversionReportReadStore
{
    private ConversionReportResult _result = new(new ConversionBucket(0, 0, 0, 0, 0, null), []);

    public SiteId? LastSiteId { get; private set; }

    public DateTimeOffset? LastFrom { get; private set; }

    public DateTimeOffset? LastTo { get; private set; }

    public void Seed(ConversionReportResult result) => _result = result;

    public Task<ConversionReportResult> GetConversionReportAsync(
        SiteId siteId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        LastSiteId = siteId;
        LastFrom = from;
        LastTo = to;

        return Task.FromResult(_result);
    }
}
