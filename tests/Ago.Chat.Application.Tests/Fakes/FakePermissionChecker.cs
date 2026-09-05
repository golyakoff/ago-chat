using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakePermissionChecker : IPermissionChecker
{
    private readonly HashSet<(OperatorId, SiteId, Permission)> _granted = [];
    private readonly HashSet<OperatorId> _removed = [];

    public void Grant(OperatorId operatorId, SiteId siteId, Permission permission) =>
        _granted.Add((operatorId, siteId, permission));

    /// <summary>`23-26`: the real <c>PermissionChecker.CountNonRemovedHoldersAsync</c> reads `RemovedAt`
    /// straight off the `operators` table it joins against - this fake has no such table to join, so a
    /// test that needs a granted holder excluded from the count (an operator removed by an earlier step
    /// in the same test, rather than by the handler under test itself) says so explicitly here.</summary>
    public void MarkRemoved(OperatorId operatorId) => _removed.Add(operatorId);

    public Task<bool> HasPermissionAsync(
        OperatorId operatorId, SiteId siteId, Permission permission, CancellationToken cancellationToken) =>
        Task.FromResult(_granted.Contains((operatorId, siteId, permission)));

    public Task<IReadOnlyList<string>> GetPermissionsAsync(
        OperatorId operatorId, SiteId siteId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(_granted
            .Where(g => g.Item1 == operatorId && g.Item2 == siteId)
            .Select(g => g.Item3.Value)
            .ToList());

    public Task<int> CountNonRemovedHoldersAsync(SiteId siteId, Permission permission, CancellationToken cancellationToken) =>
        Task.FromResult(_granted.Count(g => g.Item2 == siteId && g.Item3 == permission && !_removed.Contains(g.Item1)));
}
