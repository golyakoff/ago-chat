using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeAttachmentEgressMeter : IAttachmentEgressMeter
{
    public sealed record Recorded(SiteId SiteId, DateOnly PeriodMonth, long Bytes);

    public List<Recorded> Records { get; } = [];

    public Task RecordAsync(SiteId siteId, DateOnly periodMonth, long bytes, CancellationToken cancellationToken)
    {
        Records.Add(new Recorded(siteId, periodMonth, bytes));
        return Task.CompletedTask;
    }
}
