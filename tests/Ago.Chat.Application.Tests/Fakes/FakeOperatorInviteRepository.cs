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

    public OperatorInvite? Get(OperatorInviteId id) => _byId.GetValueOrDefault(id);
}
