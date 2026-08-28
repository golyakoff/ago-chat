using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeRoleRepository : IRoleRepository
{
    private readonly Dictionary<(SiteId, string), Guid> _roleIds = [];

    public void Seed(SiteId siteId, string name, Guid roleId) => _roleIds[(siteId, name)] = roleId;

    public Task<Guid?> GetIdByNameAsync(SiteId siteId, string name, CancellationToken cancellationToken) =>
        Task.FromResult(_roleIds.TryGetValue((siteId, name), out var roleId) ? roleId : (Guid?)null);
}
