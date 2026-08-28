using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Mirrors the real <c>ExportRequestRepository</c>'s own contract: <see cref="CreateAsync"/>
/// fails for a site that was never seeded, and <see cref="GetAsync"/> is scoped by both the export id
/// and the site id, the same cross-tenant guard <see cref="FakeErasureRequestRepository"/> establishes
/// for its own two methods.</summary>
public sealed class FakeExportRequestRepository : IExportRequestRepository
{
    private readonly HashSet<SiteId> _existingSites = [];
    private readonly Dictionary<Guid, (SiteId SiteId, ExportRequestRecord Record)> _requests = [];

    public void SeedSite(SiteId siteId) => _existingSites.Add(siteId);

    public IReadOnlyDictionary<Guid, (SiteId SiteId, ExportRequestRecord Record)> Requests => _requests;

    public void SetReady(Guid exportId, string objectKey, DateTimeOffset completedAt)
    {
        var (siteId, record) = _requests[exportId];
        _requests[exportId] = (siteId, record with { Status = ExportStatus.Ready, ObjectKey = objectKey, CompletedAt = completedAt });
    }

    public Task<bool> CreateAsync(
        Guid exportId, SiteId siteId, OperatorId requestedBy, DateTimeOffset requestedAt, CancellationToken cancellationToken)
    {
        if (!_existingSites.Contains(siteId))
        {
            return Task.FromResult(false);
        }

        _requests[exportId] = (siteId, new ExportRequestRecord(exportId, ExportStatus.Pending, null, null, requestedAt, null));
        return Task.FromResult(true);
    }

    public Task<ExportRequestRecord?> GetAsync(Guid exportId, SiteId siteId, CancellationToken cancellationToken)
    {
        if (!_requests.TryGetValue(exportId, out var entry) || entry.SiteId != siteId)
        {
            return Task.FromResult<ExportRequestRecord?>(null);
        }

        return Task.FromResult<ExportRequestRecord?>(entry.Record);
    }
}
