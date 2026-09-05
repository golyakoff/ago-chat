using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>An in-memory <see cref="IAccessRecordRepository"/> that records every call it receives,
/// so a handler test can assert both "a row was written" and "the row names the right actor/resource" -
/// and, just as importantly, that a refused or not-found path writes none at all.</summary>
public sealed class FakeAccessRecordRepository : IAccessRecordRepository
{
    private readonly List<AccessRecordToWrite> _recorded = [];

    public IReadOnlyList<AccessRecordToWrite> Recorded => _recorded;

    public Task RecordAsync(AccessRecordToWrite record, CancellationToken cancellationToken)
    {
        _recorded.Add(record);
        return Task.CompletedTask;
    }

    public Task<AccessRecordPage> ListForSiteAsync(
        SiteId siteId, Guid? beforeId, int limit, CancellationToken cancellationToken)
    {
        var items = _recorded
            .Where(r => r.SiteId == siteId)
            .OrderByDescending(r => r.Id)
            .Where(r => beforeId is null || r.Id.CompareTo(beforeId.Value) < 0)
            .Take(limit)
            .Select(r => new AccessRecordItem(r.Id, r.OccurredAt, r.AccessKind, r.ActorKind, r.ActorId, r.ResourceKind, r.ResourceId))
            .ToList();

        return Task.FromResult(new AccessRecordPage(items, null));
    }
}
