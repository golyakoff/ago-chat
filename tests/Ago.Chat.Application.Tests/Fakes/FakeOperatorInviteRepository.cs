using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>`26-241`: also implements <see cref="IPendingOperatorInviteSeatReadStore"/> so the same
/// in-memory store both saves invites and answers the create-time pending-seat count over them - a test
/// can seed an outstanding invite through <see cref="SaveAsync"/> and prove the next create reserves
/// against it, without a real Postgres row.</summary>
public sealed class FakeOperatorInviteRepository : IOperatorInviteRepository, IPendingOperatorInviteSeatReadStore
{
    private readonly Dictionary<OperatorInviteId, OperatorInvite> _byId = [];

    public Task SaveAsync(OperatorInvite invite, CancellationToken cancellationToken)
    {
        _byId[invite.Id] = invite;
        return Task.CompletedTask;
    }

    public Task<OperatorInvite?> GetByIdAsync(OperatorInviteId id, CancellationToken cancellationToken) =>
        Task.FromResult(_byId.GetValueOrDefault(id));

    public OperatorInvite? Get(OperatorInviteId id) => _byId.GetValueOrDefault(id);

    /// <summary>`25-73`: how many invites this fake actually holds - used by tests proving a refused
    /// `CreateOperatorInviteHandler` call (invalid email, rate-limited) never reached `SaveAsync` at
    /// all, where there is no id to `Get` in the first place.</summary>
    public int Count => _byId.Count;

    /// <summary>`26-241`: the same "outstanding" predicate the real
    /// <see cref="PendingOperatorInviteSeatReadStore"/> applies - unredeemed, unrevoked, unexpired (as of
    /// <paramref name="now"/>), and granting <paramref name="roleId"/> - counted over whatever invites
    /// this fake has been handed through <see cref="SaveAsync"/>.</summary>
    public Task<int> CountOutstandingForRoleAsync(
        SiteId siteId, Guid roleId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var count = _byId.Values.Count(invite =>
            invite.SiteId == siteId
            && !invite.IsRedeemed
            && !invite.IsRevoked
            && !invite.IsExpired(now)
            && invite.RoleIds.Contains(roleId));
        return Task.FromResult(count);
    }
}
