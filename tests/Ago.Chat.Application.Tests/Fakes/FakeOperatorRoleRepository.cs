using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeOperatorRoleRepository : IOperatorRoleRepository
{
    private readonly Dictionary<OperatorId, List<string>> _roleNames = [];
    private readonly Dictionary<OperatorId, Guid> _currentRoleId = [];

    public void Seed(OperatorId operatorId, params string[] roleNames) => _roleNames[operatorId] = [.. roleNames];

    public Task<IReadOnlyList<string>> GetRoleNamesAsync(OperatorId operatorId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(_roleNames.TryGetValue(operatorId, out var names) ? names : []);

    public Task ReplaceRoleAsync(OperatorId operatorId, Guid roleId, CancellationToken cancellationToken)
    {
        _currentRoleId[operatorId] = roleId;
        return Task.CompletedTask;
    }

    /// <summary>What the last <see cref="ReplaceRoleAsync"/> call left this operator holding - lets a
    /// test assert the write happened without a real Postgres row to query back.</summary>
    public Guid? CurrentRoleId(OperatorId operatorId) => _currentRoleId.TryGetValue(operatorId, out var id) ? id : null;
}
