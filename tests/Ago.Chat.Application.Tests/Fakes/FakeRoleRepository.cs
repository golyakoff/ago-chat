using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeRoleRepository : IRoleRepository
{
    private readonly Dictionary<(SiteId, string), Guid> _roleIds = [];

    /// <summary>`23-102`: what <see cref="AddPermissionsAsync"/> actually did - keyed the same way
    /// <see cref="_roleIds"/> is, so a test can assert on one site/role pair without caring about every
    /// other role this fixture happens to carry.</summary>
    private readonly Dictionary<(SiteId, string), HashSet<string>> _permissions = [];

    public void Seed(SiteId siteId, string name, Guid roleId) => _roleIds[(siteId, name)] = roleId;

    /// <summary>`23-72`: the permission-carrying overload <see cref="ChangeOperatorRoleHandlerTests"/>
    /// needs - <see cref="GetByNameAsync"/> answers both the id and the permission set the real
    /// <c>RoleRepository</c> would, so a test can seed a role that does or does not grant
    /// `site:manage_operators`.</summary>
    public void Seed(SiteId siteId, string name, Guid roleId, IReadOnlyList<string> permissions)
    {
        _roleIds[(siteId, name)] = roleId;
        _permissions[(siteId, name)] = [.. permissions];
    }

    /// <summary>`23-102`: seeds a role's *starting* permission set - the union
    /// <see cref="AddPermissionsAsync"/> then grows, the same shape a real site's roles carry before a
    /// module grant ever runs.</summary>
    public void SeedPermissions(SiteId siteId, string name, params string[] permissions) =>
        _permissions[(siteId, name)] = [.. permissions];

    public Task<Guid?> GetIdByNameAsync(SiteId siteId, string name, CancellationToken cancellationToken) =>
        Task.FromResult(_roleIds.TryGetValue((siteId, name), out var roleId) ? roleId : (Guid?)null);

    public Task AddPermissionsAsync(
        SiteId siteId, string roleName, IReadOnlyCollection<string> permissions, CancellationToken cancellationToken)
    {
        if (permissions.Count == 0)
        {
            return Task.CompletedTask;
        }

        if (!_permissions.TryGetValue((siteId, roleName), out var current))
        {
            current = [];
            _permissions[(siteId, roleName)] = current;
        }

        foreach (var permission in permissions)
        {
            current.Add(permission);
        }

        return Task.CompletedTask;
    }

    /// <summary>What a test reads back - the same "empty set for a role nothing ever touched" default a
    /// real site's role would never actually have (every real role is seeded at registration), kept here
    /// only so a lookup on an unseeded pair does not throw.</summary>
    public IReadOnlySet<string> PermissionsFor(SiteId siteId, string roleName) =>
        _permissions.TryGetValue((siteId, roleName), out var current) ? current : new HashSet<string>();

    /// <summary>`25-77`: what <see cref="RemovePermissionsAsync"/> actually recorded - one entry per
    /// call that found a real role, in call order, the fake's own equivalent of a
    /// `role_permission_removal_overrides` row - so a test can assert both that a removal happened and
    /// what reason it carried, without a database.</summary>
    public List<(SiteId SiteId, string RoleName, IReadOnlyList<string> Permissions, string RemovedBy, string Reason)> RemovalOverrides { get; } = [];

    public Task RemovePermissionsAsync(
        SiteId siteId, string roleName, IReadOnlyCollection<string> permissions, string removedBy, string reason,
        CancellationToken cancellationToken)
    {
        if (permissions.Count == 0)
        {
            return Task.CompletedTask;
        }

        if (!_roleIds.ContainsKey((siteId, roleName)))
        {
            // No role by that name - the identical "nothing to do, nothing to attest to" branch the
            // real RoleRepository's own remarks state for this case.
            return Task.CompletedTask;
        }

        if (_permissions.TryGetValue((siteId, roleName), out var current))
        {
            foreach (var permission in permissions)
            {
                current.Remove(permission);
            }
        }

        RemovalOverrides.Add((siteId, roleName, [.. permissions], removedBy, reason));
        return Task.CompletedTask;
    }

    /// <summary>`23-72`: `ChangeOperatorRoleHandler`'s own lookup - returns both the id and the
    /// permission set for a role seeded by either <see cref="Seed(SiteId,string,Guid,IReadOnlyList{string})"/>
    /// or grown afterward by <see cref="AddPermissionsAsync"/>/<see cref="SeedPermissions"/>, so the two
    /// sides of this fake never disagree about what a role currently carries.</summary>
    public Task<RoleLookup?> GetByNameAsync(SiteId siteId, string name, CancellationToken cancellationToken)
    {
        if (!_roleIds.TryGetValue((siteId, name), out var roleId))
        {
            return Task.FromResult<RoleLookup?>(null);
        }

        IReadOnlyList<string> permissions = _permissions.TryGetValue((siteId, name), out var current)
            ? [.. current]
            : [];
        return Task.FromResult<RoleLookup?>(new RoleLookup(roleId, permissions));
    }

    /// <summary>`25-76`: every role seeded for <paramref name="siteId"/> - the fake's own equivalent of
    /// the real repository's single `roles` query, built from the identical `_roleIds`/`_permissions`
    /// state every other method here already reads.</summary>
    public Task<IReadOnlyList<RoleSummary>> GetAllForSiteAsync(SiteId siteId, CancellationToken cancellationToken)
    {
        IReadOnlyList<RoleSummary> rows = _roleIds
            .Where(entry => entry.Key.Item1 == siteId)
            .OrderBy(entry => entry.Key.Item2, StringComparer.Ordinal)
            .Select(entry => new RoleSummary(
                entry.Key.Item2,
                entry.Value,
                _permissions.TryGetValue(entry.Key, out var permissions) ? [.. permissions] : []))
            .ToList();
        return Task.FromResult(rows);
    }
}
