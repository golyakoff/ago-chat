using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `14-14`: the write-side port for <see cref="VisitorContactDetail"/> - shaped by the three use cases
/// that need it, never a generic <c>IRepository&lt;T&gt;</c> (clean-architecture.md).
///
/// <para><b>This is the leak-proofing, made structural</b> - the same standard <see cref="INoteRepository"/>'s
/// own remarks hold itself to. This interface shares no method, no base type and no implementation
/// with <see cref="IChannelIdentityRepository"/>, and nothing it returns is ever handed to
/// <c>DeliverChannelMessageHandler</c> or anything that constructs an <see cref="ExternalChannelAddress"/>.
/// There is no line of code anywhere in this codebase's call graph that reaches from a
/// <see cref="VisitorContactDetail"/> to a real send - not a filtered-out branch, an absent
/// one.</para>
/// </summary>
public interface IVisitorContactDetailRepository
{
    Task SaveAsync(VisitorContactDetail detail, CancellationToken cancellationToken);

    /// <summary>Every contact detail recorded for this visitor, oldest first - an operator's own
    /// working notes, small and bounded (nobody records hundreds of these per visitor), the same
    /// "plain unbounded list" shape <see cref="INoteRepository.GetForConversationAsync"/> already
    /// justifies for itself.</summary>
    Task<IReadOnlyList<VisitorContactDetail>> GetForVisitorAsync(VisitorId visitorId, CancellationToken cancellationToken);

    /// <summary>By primary key, for <c>DeleteVisitorContactDetailHandler</c> - the caller does not yet
    /// know which visitor the id belongs to, only the id the console showed; the handler checks the
    /// returned row's own <see cref="VisitorContactDetail.VisitorId"/> against the conversation it was
    /// asked through before deleting anything, the same "wrong visitor reads like no row" info-hiding
    /// shape <c>IChannelIdentityRepository.GetByIdAsync</c>'s own callers already use for a different
    /// aggregate.</summary>
    Task<VisitorContactDetail?> GetByIdAsync(VisitorContactDetailId id, CancellationToken cancellationToken);

    /// <summary>A real row removal, not a soft-delete flip - <see cref="VisitorContactDetail"/>'s own
    /// remarks on why this type carries no <c>Active</c>/<c>DeletedAt</c> pair to write instead.</summary>
    Task DeleteAsync(VisitorContactDetail detail, CancellationToken cancellationToken);
}
