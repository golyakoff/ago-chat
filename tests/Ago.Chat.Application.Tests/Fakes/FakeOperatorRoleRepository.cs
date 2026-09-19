using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeOperatorRoleRepository : IOperatorRoleRepository
{
    private readonly Dictionary<OperatorId, List<string>> _roleNames = [];
    private readonly Dictionary<OperatorId, Guid> _currentRoleId = [];
    // `25-170`: per-(operator, role) seat status/grant time - absent means "holds a seat, granted at
    // the epoch" (DateTimeOffset.MinValue), the identical default every real row this codebase writes
    // starts from (OperatorRoleRecord's own remarks), so a bare Seed(operatorId, roleNames) call keeps
    // meaning exactly what every test written before this item already expects.
    private readonly Dictionary<(OperatorId OperatorId, string RoleName), bool> _holdsSeat = [];
    private readonly Dictionary<(OperatorId OperatorId, string RoleName), DateTimeOffset> _grantedAt = [];
    // `25-170`: a real OperatorRoleRepository query joins against `operators.removed_at IS NULL` - this
    // fake has no join to the real FakeOperatorRepository's own seeded rows, so a test that needs a
    // removed operator excluded from a held-seat count marks it here explicitly.
    private readonly HashSet<OperatorId> _removed = [];
    // `25-170`: lets ReplaceRoleAsync (keyed by Guid, matching the real port) resolve which role name it
    // was actually handed, so it can carry the operator's own existing seat status forward onto the
    // freshly assigned role the same way the real OperatorRoleRepository does - a test registers a role
    // id/name pair once via RegisterRoleId, mirroring whatever FakeRoleRepository was seeded with.
    private readonly Dictionary<Guid, string> _roleNamesById = [];

    public void Seed(OperatorId operatorId, params string[] roleNames) => _roleNames[operatorId] = [.. roleNames];

    /// <summary>`25-170`: registers a role id/name pair so <see cref="ReplaceRoleAsync"/> can resolve
    /// its own <c>Guid</c> parameter back to a name - call this with the identical id a test's own
    /// `FakeRoleRepository.Seed` call used for the same role.</summary>
    public void RegisterRoleId(string roleName, Guid roleId) => _roleNamesById[roleId] = roleName;

    /// <summary>`25-170`: excludes this operator from every held-seat query, mirroring
    /// `operators.removed_at IS NOT NULL` - the fake-only counterpart of seeding a removed
    /// <see cref="Operator"/> on <see cref="FakeOperatorRepository"/>.</summary>
    public void MarkRemoved(OperatorId operatorId) => _removed.Add(operatorId);

    /// <summary>`25-170`: seeds one role assignment's own seat status and grant time explicitly - for
    /// tests exercising the per-role seat behaviour this item adds (disabling one role's seat without
    /// touching the other's, reconciliation ordering by <c>GrantedAt</c>). Also registers the role
    /// itself via <see cref="Seed"/>'s own list if it is not already there.</summary>
    public void SeedSeat(OperatorId operatorId, string roleName, bool holdsSeat, DateTimeOffset? grantedAt = null)
    {
        if (!_roleNames.TryGetValue(operatorId, out var names))
        {
            names = [];
            _roleNames[operatorId] = names;
        }

        if (!names.Contains(roleName))
        {
            names.Add(roleName);
        }

        _holdsSeat[(operatorId, roleName)] = holdsSeat;
        if (grantedAt is { } g)
        {
            _grantedAt[(operatorId, roleName)] = g;
        }
    }

    public Task<IReadOnlyList<string>> GetRoleNamesAsync(OperatorId operatorId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(_roleNames.TryGetValue(operatorId, out var names) ? names : []);

    public Task ReplaceRoleAsync(OperatorId operatorId, Guid roleId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        _currentRoleId[operatorId] = roleId;

        // `25-170`: mirrors the real OperatorRoleRepository.ReplaceRoleAsync - "any" of the operator's
        // existing roles held a seat carries forward onto the freshly assigned one, never reset to a
        // fresh default. Only actually replaces this fake's own role-name tracking when the caller has
        // registered the id/name mapping (RegisterRoleId) - a test that only asserts CurrentRoleId still
        // works unchanged without it.
        if (_roleNamesById.TryGetValue(roleId, out var newRoleName))
        {
            var holdsSeat = _roleNames.TryGetValue(operatorId, out var existingNames)
                && existingNames.Any(name => HoldsSeat(operatorId, name));
            _roleNames[operatorId] = [newRoleName];
            _holdsSeat[(operatorId, newRoleName)] = holdsSeat;
            _grantedAt[(operatorId, newRoleName)] = now;
        }

        return Task.CompletedTask;
    }

    /// <summary>What the last <see cref="ReplaceRoleAsync"/> call left this operator holding - lets a
    /// test assert the write happened without a real Postgres row to query back.</summary>
    public Guid? CurrentRoleId(OperatorId operatorId) => _currentRoleId.TryGetValue(operatorId, out var id) ? id : null;

    public Task<bool> HoldsAnySeatAsync(OperatorId operatorId, CancellationToken cancellationToken) =>
        Task.FromResult(_roleNames.TryGetValue(operatorId, out var names) && names.Any(n => HoldsSeat(operatorId, n)));

    public Task<bool> HoldsRoleSeatAsync(OperatorId operatorId, SiteId siteId, string roleName, CancellationToken cancellationToken) =>
        Task.FromResult(_roleNames.TryGetValue(operatorId, out var names) && names.Contains(roleName) && HoldsSeat(operatorId, roleName));

    /// <summary>`25-170`: the identical `_roleNames` seed this fake already keeps for
    /// <see cref="GetRoleNamesAsync"/> - no separate lock/site-row simulation, since this fake has no
    /// transaction to serialize against and no Application-layer test here exercises concurrent callers
    /// (that proof is `Ago.Chat.Concurrency.Tests`' own job, against real Postgres, the same split every
    /// other lock-shaped fake in this project already draws).</summary>
    public Task<IReadOnlyList<OperatorId>> GetHeldSeatHolderIdsAsync(SiteId siteId, string roleName, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OperatorId>>(HeldSeatHolders(roleName));

    public Task<IReadOnlyList<OperatorId>> LockAndGetHeldSeatHolderIdsAsync(SiteId siteId, string roleName, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OperatorId>>(HeldSeatHolders(roleName));

    public Task SetHoldsSeatAsync(OperatorId operatorId, SiteId siteId, string roleName, bool holdsSeat, CancellationToken cancellationToken)
    {
        _holdsSeat[(operatorId, roleName)] = holdsSeat;
        return Task.CompletedTask;
    }

    private bool HoldsSeat(OperatorId operatorId, string roleName) =>
        _holdsSeat.TryGetValue((operatorId, roleName), out var value) ? value : true;

    private List<OperatorId> HeldSeatHolders(string roleName) =>
        _roleNames
            .Where(kv => kv.Value.Contains(roleName) && HoldsSeat(kv.Key, roleName) && !_removed.Contains(kv.Key))
            .OrderByDescending(kv => _grantedAt.TryGetValue((kv.Key, roleName), out var granted) ? granted : DateTimeOffset.MinValue)
            .ThenBy(kv => kv.Key.Value)
            .Select(kv => kv.Key)
            .ToList();
}
