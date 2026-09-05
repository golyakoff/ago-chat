using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeModuleRevokeOverrideRepository : IModuleRevokeOverrideRepository
{
    private readonly List<ModuleRevokeOverrideRecord> _records = [];

    public IReadOnlyList<ModuleRevokeOverrideRecord> Records => _records;

    public Task RecordAsync(
        Guid id, SiteId siteId, string moduleKey, string revokedBy, string reason, DateTimeOffset revokedAt,
        CancellationToken cancellationToken)
    {
        _records.Add(new ModuleRevokeOverrideRecord(id, siteId, moduleKey, revokedBy, reason, revokedAt));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ModuleRevokeOverrideRecord>> ListForSiteAsync(SiteId siteId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ModuleRevokeOverrideRecord>>(
            [.. _records.Where(r => r.SiteId == siteId).OrderBy(r => r.RevokedAt)]);
}
