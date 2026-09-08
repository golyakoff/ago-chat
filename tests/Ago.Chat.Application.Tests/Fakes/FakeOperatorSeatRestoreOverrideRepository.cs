using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeOperatorSeatRestoreOverrideRepository : IOperatorSeatRestoreOverrideRepository
{
    private readonly List<OperatorSeatRestoreOverrideRecord> _records = [];

    public IReadOnlyList<OperatorSeatRestoreOverrideRecord> Records => _records;

    public Task RecordAsync(
        Guid id, SiteId siteId, OperatorId operatorId, string restoredBy, string reason, DateTimeOffset restoredAt,
        CancellationToken cancellationToken)
    {
        _records.Add(new OperatorSeatRestoreOverrideRecord(id, siteId, operatorId, restoredBy, reason, restoredAt));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OperatorSeatRestoreOverrideRecord>> ListForSiteAsync(SiteId siteId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OperatorSeatRestoreOverrideRecord>>(
            [.. _records.Where(r => r.SiteId == siteId).OrderBy(r => r.RestoredAt)]);
}
