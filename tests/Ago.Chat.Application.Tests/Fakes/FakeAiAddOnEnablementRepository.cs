using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>`25-04`: an in-memory <see cref="IAiAddOnEnablementRepository"/>. Also implements
/// <see cref="IAiAddOnReadStore"/> so a test that enables the add-on through the real handler and then
/// asks the real gate is reading one state, not two that could disagree.</summary>
public sealed class FakeAiAddOnEnablementRepository : IAiAddOnEnablementRepository, IAiAddOnReadStore
{
    private readonly Dictionary<SiteId, AiAddOnEnablement> _rows = [];

    /// <summary>Synchronous seeding for arrange blocks - rule 3 forbids `.GetAwaiter().GetResult()`
    /// even over a fake whose task is already completed.</summary>
    public void Seed(AiAddOnEnablement enablement) => _rows[enablement.SiteId] = enablement;

    public Task<AiAddOnEnablement?> GetForSiteAsync(SiteId siteId, CancellationToken cancellationToken) =>
        Task.FromResult(_rows.GetValueOrDefault(siteId));

    public Task SaveAsync(AiAddOnEnablement enablement, CancellationToken cancellationToken)
    {
        _rows[enablement.SiteId] = enablement;
        return Task.CompletedTask;
    }

    Task<AiAddOnEnablementState?> IAiAddOnReadStore.GetForSiteAsync(SiteId siteId, CancellationToken cancellationToken) =>
        Task.FromResult(_rows.TryGetValue(siteId, out var row)
            ? new AiAddOnEnablementState(row.IsEnabled, row.EffectiveFrom)
            : null);
}
