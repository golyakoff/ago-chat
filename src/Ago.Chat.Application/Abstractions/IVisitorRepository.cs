using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// Persists <see cref="Visitor"/> - separate from <see cref="IConversationRepository"/> because a
/// visitor's identity outlives any single conversation (data-model.md: "may return days later and
/// see their history").
/// </summary>
public interface IVisitorRepository
{
    Task<Visitor?> GetByIdAsync(VisitorId id, CancellationToken cancellationToken);

    Task SaveAsync(Visitor visitor, CancellationToken cancellationToken);
}
