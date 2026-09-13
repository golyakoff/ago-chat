using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// The write-side port for the <see cref="Attachment"/> aggregate - its own port, not folded into
/// <see cref="IConversationRepository"/>, because `5-03` made <see cref="Attachment"/> its own
/// aggregate root with its own transaction boundary (see that type's own remarks).
/// </summary>
public interface IAttachmentRepository
{
    Task<Attachment?> GetByIdAsync(AttachmentId id, CancellationToken cancellationToken);

    Task SaveAsync(Attachment attachment, CancellationToken cancellationToken);

    /// <summary>`23-76`: the dedup lookup - is there already a <see cref="AttachmentState.Ready"/>
    /// attachment for this <paramref name="siteId"/> carrying this exact <paramref name="contentHash"/>,
    /// other than <paramref name="excluding"/> itself. Scoped to the tenant only, never across sites -
    /// `adr/0108`'s own rewrite-not-delete reasoning is why a shared object between two tenants is not
    /// this item's safe first version. Ties (more than one existing match, which can only happen if two
    /// uploads of the same content were confirmed concurrently before either had set its own hash) are
    /// broken by earliest <c>CreatedAt</c> - the oldest copy is treated as canonical, so a chain of
    /// three or more identical uploads still converges onto one object rather than each pointing at
    /// whichever the previous one happened to see.</summary>
    Task<Attachment?> FindReadyDuplicateAsync(
        SiteId siteId, string contentHash, AttachmentId excluding, CancellationToken cancellationToken);
}
