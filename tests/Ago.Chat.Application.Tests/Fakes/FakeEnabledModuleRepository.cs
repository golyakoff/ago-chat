using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeEnabledModuleRepository : IEnabledModuleRepository
{
    private readonly Dictionary<EnabledModuleId, EnabledModule> _byId = [];

    public Task<EnabledModule?> GetAsync(SiteId siteId, ModuleKey moduleKey, CancellationToken cancellationToken) =>
        Task.FromResult(_byId.Values.FirstOrDefault(m => m.SiteId == siteId && m.ModuleKey == moduleKey));

    public Task SaveAsync(EnabledModule module, CancellationToken cancellationToken)
    {
        _byId[module.Id] = module;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(EnabledModule module, CancellationToken cancellationToken)
    {
        _byId[module.Id] = module;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(EnabledModuleId id, CancellationToken cancellationToken)
    {
        _byId.Remove(id);
        return Task.CompletedTask;
    }

    public IReadOnlyList<EnabledModule> All => [.. _byId.Values];
}
