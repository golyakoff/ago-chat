using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Records the exact bound `GetTagBreakdownReportForSiteHandler` resolved and hands back
/// whatever this fake was seeded with - the same shape `FakeConversionReportReadStore` already
/// establishes for `18-10`'s analogous handler.</summary>
public sealed class FakeTagBreakdownReadStore : ITagBreakdownReadStore
{
    private TagBreakdownResult _result = new(0, 0, null, []);

    public SiteId? LastSiteId { get; private set; }

    public DateTimeOffset? LastFrom { get; private set; }

    public DateTimeOffset? LastTo { get; private set; }

    public void Seed(TagBreakdownResult result) => _result = result;

    public Task<TagBreakdownResult> GetTagBreakdownAsync(
        SiteId siteId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        LastSiteId = siteId;
        LastFrom = from;
        LastTo = to;

        return Task.FromResult(_result);
    }
}
