using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeVisitorRepository : IVisitorRepository
{
    private readonly Dictionary<VisitorId, Visitor> _byId = [];

    public Task<Visitor?> GetByIdAsync(VisitorId id, CancellationToken cancellationToken) =>
        Task.FromResult(_byId.GetValueOrDefault(id));

    public Task SaveAsync(Visitor visitor, CancellationToken cancellationToken)
    {
        _byId[visitor.Id] = visitor;
        return Task.CompletedTask;
    }
}
