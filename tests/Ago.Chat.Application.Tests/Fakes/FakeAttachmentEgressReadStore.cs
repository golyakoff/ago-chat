using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>`25-83`: the read half a hard-block test needs to set up "this tenant has already
/// downloaded this many bytes this month" without a real Postgres row - <see cref="IAttachmentEgressReadStore"/>'s
/// own contract, honoured in memory (a missing key reads as the honest zero that store's own remarks
/// describe for a real database miss).</summary>
public sealed class FakeAttachmentEgressReadStore : IAttachmentEgressReadStore
{
    private readonly Dictionary<(SiteId SiteId, DateOnly PeriodMonth), long> _bytesOut = [];

    public void SeedBytesOut(SiteId siteId, DateOnly periodMonth, long bytesOut) =>
        _bytesOut[(siteId, periodMonth)] = bytesOut;

    public Task<SiteAttachmentEgress> GetForSiteAsync(SiteId siteId, DateOnly periodMonth, CancellationToken cancellationToken)
    {
        var bytesOut = _bytesOut.GetValueOrDefault((siteId, periodMonth));
        return Task.FromResult(new SiteAttachmentEgress(siteId, periodMonth, bytesOut > 0 ? 1 : 0, bytesOut));
    }
}
