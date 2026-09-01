using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeModuleTaskChannelPreferenceRepository : IModuleTaskChannelPreferenceRepository
{
    private readonly List<ModuleTaskChannelPreference> _rows = [];

    public IReadOnlyCollection<ModuleTaskChannelPreference> All => _rows;

    public Task<IReadOnlyList<ModuleTaskChannelPreference>> ListForModuleTaskAsync(
        ModuleTaskId moduleTaskId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ModuleTaskChannelPreference>>(
            [.. _rows.Where(r => r.ModuleTaskId == moduleTaskId).OrderBy(r => r.Priority)]);

    public Task ReplaceForModuleTaskAsync(
        ModuleTaskId moduleTaskId, IReadOnlyList<ModuleTaskChannelPreference> preferences, CancellationToken cancellationToken)
    {
        _rows.RemoveAll(r => r.ModuleTaskId == moduleTaskId);
        _rows.AddRange(preferences);
        return Task.CompletedTask;
    }

    public void Seed(ModuleTaskChannelPreference preference) => _rows.Add(preference);
}
