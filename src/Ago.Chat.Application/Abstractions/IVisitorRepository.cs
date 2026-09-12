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

    /// <summary>
    /// `25-56`: <see cref="GetOperatorQueueHandler"/>'s own need - it loads full
    /// <see cref="Domain.Conversation"/> aggregates for its two lists (this DTO's own remarks explain
    /// why), which carry a <see cref="VisitorId"/> but never the <see cref="Visitor"/> itself, so
    /// rendering each row's emoji pair needs one more read. A batch by distinct id, not a loop of
    /// <see cref="GetByIdAsync"/> calls - the queue's own two lists are already "small, bounded,
    /// unpaginated" (that handler's own remarks on its in-memory tag filter), so one extra round trip
    /// for every distinct visitor across both lists costs nothing a per-row round trip would not have
    /// cost far more of.
    /// </summary>
    Task<IReadOnlyDictionary<VisitorId, Visitor>> GetManyByIdsAsync(
        IReadOnlyCollection<VisitorId> ids, CancellationToken cancellationToken);

    Task SaveAsync(Visitor visitor, CancellationToken cancellationToken);
}
