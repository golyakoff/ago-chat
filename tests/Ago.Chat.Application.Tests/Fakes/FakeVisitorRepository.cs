using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeVisitorRepository : IVisitorRepository
{
    private readonly Dictionary<VisitorId, Visitor> _byId = [];

    /// <summary>`14-13`: a synchronous seed for test setup - <c>FakeConversationRepository.Seed</c>'s
    /// own precedent, so a harness building fixture state does not need to be <see langword="async"/>
    /// (and never needs `.GetAwaiter().GetResult()`, which CLAUDE.md bans outside Infrastructure).</summary>
    public void Seed(Visitor visitor) => _byId[visitor.Id] = visitor;

    public Task<Visitor?> GetByIdAsync(VisitorId id, CancellationToken cancellationToken) =>
        Task.FromResult(_byId.GetValueOrDefault(id));

    public Task SaveAsync(Visitor visitor, CancellationToken cancellationToken)
    {
        _byId[visitor.Id] = visitor;
        return Task.CompletedTask;
    }
}
