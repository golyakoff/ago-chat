using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakePermissionChecker : IPermissionChecker
{
    private readonly HashSet<(OperatorId, SiteId, Permission)> _granted = [];

    public void Grant(OperatorId operatorId, SiteId siteId, Permission permission) =>
        _granted.Add((operatorId, siteId, permission));

    public Task<bool> HasPermissionAsync(
        OperatorId operatorId, SiteId siteId, Permission permission, CancellationToken cancellationToken) =>
        Task.FromResult(_granted.Contains((operatorId, siteId, permission)));
}
