using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// The write-side port for <see cref="Tag"/> and its association to a <see cref="Conversation"/> -
/// one interface rather than two, because every method here is shaped by the same small feature's own
/// handlers (clean-architecture.md's "shaped by the use cases that need it"), and a tag's association
/// rows have no lifecycle of their own separate from the tag and the conversation they join.
/// </summary>
public interface ITagRepository
{
    Task<Tag?> GetByIdAsync(TagId id, SiteId siteId, CancellationToken cancellationToken);

    /// <summary>Case-insensitive lookup (<see cref="StringComparison.OrdinalIgnoreCase"/>) for the
    /// duplicate-name guard (`CreateTagHandler`/`RenameTagHandler`) - implemented over
    /// <see cref="GetAllForSiteAsync"/>'s own small, bounded per-site list rather than a SQL
    /// case-insensitive predicate, since the list this scans is the same one a human already browses
    /// (`Tag.MaxNameLength`'s own remarks on "browsable, not evaluated per message"). The database's
    /// own unique index (`TagConfiguration`) is case-*sensitive*, so it is the primary defence against
    /// an exact-duplicate race and only a partial one against two different casings racing each other
    /// - a known, small residual gap stated here rather than silently assumed away, and the reason
    /// <see cref="Domain.TagNameConflictException"/> exists as the final backstop for the common
    /// case.</summary>
    Task<Tag?> GetByNameAsync(SiteId siteId, string name, CancellationToken cancellationToken);

    /// <summary>Every tag defined for a site - the management surface's own list, and the source for
    /// a queue/admin-list filter dropdown. Small and bounded (a per-site vocabulary an operator
    /// browses), the same "plain unbounded list" shape as <see cref="INoteRepository.GetForConversationAsync"/>.</summary>
    Task<IReadOnlyList<Tag>> GetAllForSiteAsync(SiteId siteId, CancellationToken cancellationToken);

    Task SaveAsync(Tag tag, CancellationToken cancellationToken);

    /// <summary>Deletes the tag definition itself. Every <c>conversation_tags</c> row naming it goes
    /// with it - the same "one line, relying on the schema's own cascade" shape
    /// <c>SiteErasureQuery.DeleteSiteAsync</c>'s own remarks describe, here scoped to one tag's FK
    /// rather than a whole site's.</summary>
    Task DeleteAsync(Tag tag, CancellationToken cancellationToken);

    /// <summary>Idempotent by design (`INSERT ... ON CONFLICT DO NOTHING` at the adapter) - tagging an
    /// already-tagged conversation a second time is a no-op, not a distinct error a caller has to
    /// avoid racing. No <c>SiteId</c> parameter: unlike <see cref="GetByIdAsync"/>, this write never
    /// resolves a row by tenant - <c>TagConversationHandler</c> has already loaded both the
    /// conversation and the tag through their own site-scoped lookups before calling this, so a third
    /// repetition of the same check here would be dead weight, not defence in depth (the write itself
    /// cannot land on the wrong tenant's row - both ids it joins are already proven to belong to the
    /// caller's site).</summary>
    Task AddToConversationAsync(ConversationId conversationId, TagId tagId, CancellationToken cancellationToken);

    /// <summary>Idempotent the same way - removing a tag that was never applied, or already removed,
    /// is a no-op.</summary>
    Task RemoveFromConversationAsync(ConversationId conversationId, TagId tagId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Tag>> GetForConversationAsync(ConversationId conversationId, CancellationToken cancellationToken);

    /// <summary>Every conversation id currently carrying this tag - `GetOperatorQueueHandler`'s own
    /// in-memory filter over the small, bounded waiting/assigned lists it already loads
    /// (`IConversationRepository`'s own remarks on why those two reads stay unpaginated). Returned as
    /// a set rather than joined into the queue query itself, because the queue's own reads go through
    /// the write-side <see cref="IConversationRepository"/>, not this port - see
    /// `GetOperatorQueueHandler`'s own remarks for why a query-level join was not the right shape
    /// here.</summary>
    Task<IReadOnlySet<ConversationId>> GetConversationIdsForTagAsync(
        TagId tagId, SiteId siteId, CancellationToken cancellationToken);

    // `16-02`: no RemoveAllFromConversationAsync here, the identical reasoning
    // INoteRepository's own remarks give - Ago.Chat.Worker's ConversationErasureJob removes
    // conversation_tags rows through ConversationErasureQuery.DeleteTagsForConversationAsync (raw
    // Npgsql), never through this Application-layer port. The tag *definitions* table (`tags`) is
    // reached a different way too - see TagConfiguration's own cascade remarks.
}
