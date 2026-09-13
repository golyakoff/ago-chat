using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeOperatorInviteRepository : IOperatorInviteRepository
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
}
