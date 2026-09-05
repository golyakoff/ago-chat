using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// The read-side port: hand-written SQL over the write model, never through the aggregate
/// (adr/0004). Keyset-shaped from the start - <paramref name="beforeSequence"/><c>null</c> means
/// "most recent page."
///
/// <para><b>`15-09`/`adr/0087`: <paramref name="siteId"/> on <see cref="GetHistoryAsync"/>/
/// <see cref="GetDeltaAsync"/>.</b> Before this item, `messages` had no partition key that a
/// conversation-scoped query could prune on, so these two methods - the most frequent query in the
/// product, one per conversation open - took only a `ConversationId`. Now that `messages` is
/// `PARTITION BY HASH (site_id)`, a query with no `site_id` predicate silently visits all 64 buckets
/// instead of the one it needs (`adr/0087`'s own "the failure mode is a performance cliff, not an
/// error"). Both callers (`GetConversationHistoryHandler`) already load the `Conversation` aggregate -
/// which carries `SiteId` - before reaching either method, so threading it through costs nothing at
/// the call site.</para>
/// </summary>
public interface IConversationReadStore
{
    Task<ConversationHistoryPage> GetHistoryAsync(
        ConversationId conversationId, SiteId siteId, int? beforeSequence, int pageSize, CancellationToken cancellationToken);

    /// <summary>
    /// `3-03`'s reconnect delta - every message strictly after <paramref name="afterSequence"/>,
    /// oldest first. Unbounded rather than keyset-paginated like <see cref="GetHistoryAsync"/>: the
    /// gap this closes is bounded by how long *one* client was disconnected, not by the
    /// conversation's whole history, so it does not carry the unbounded-result risk that
    /// <see cref="GetHistoryAsync"/>'s "load older messages" direction guards against by paging.
    /// </summary>
    Task<IReadOnlyList<MessageHistoryItem>> GetDeltaAsync(
        ConversationId conversationId, SiteId siteId, int afterSequence, CancellationToken cancellationToken);

    /// <summary>
    /// `5-08`: the admin/supervisor view's own read - every conversation for a site regardless of
    /// state or assignment, unlike <see cref="IConversationRepository.GetWaitingForSiteAsync"/>/
    /// <see cref="IConversationRepository.GetAssignedToOperatorAsync"/> (bounded, state-filtered
    /// lists small enough that going through the write-side EF repository was the right call - see
    /// that interface's own remarks). A site's full conversation history carries no such bound; it
    /// only grows, which is exactly the "paginated, potentially-large read" case this read store
    /// exists for, so this lives here rather than as a third method on the write-side repository.
    /// </summary>
    /// <summary><paramref name="tagId"/>: `18-04`'s own list filter, <see langword="null"/> means
    /// unfiltered - pushed into this method's own query rather than applied after paging, unlike
    /// `GetOperatorQueueHandler`'s in-memory filter over its two small, unpaginated reads. This read
    /// is the one genuinely paginated list on this table (an admin's whole site history, unbounded),
    /// so filtering after a page was already cut would return fewer than <paramref name="pageSize"/>
    /// items whenever a tag is rare, with no way for the caller to tell "this page is short" apart
    /// from "this is the last page".</summary>
    Task<ConversationListPage> GetAllForSiteAsync(
        SiteId siteId, Guid? beforeId, int pageSize, TagId? tagId, CancellationToken cancellationToken);

    /// <summary>
    /// `16-02`: one conversation by id, scoped to <paramref name="siteId"/> - <see langword="null"/>
    /// if it does not exist, or belongs to a different site (indistinguishable from each other, the
    /// same not-found-not-forbidden choice <see cref="Application.UseCases.RequestConversationErasure.RequestConversationErasureHandler"/>
    /// makes). Its own query rather than reusing <see cref="GetAllForSiteAsync"/> with a filter: that
    /// method pages a list and this is a point lookup, and its only real caller
    /// (<c>GetConversationByIdHandler</c>) needs it for exactly one purpose - letting the console poll
    /// a conversation until it 404s after requesting its erasure, `16-02`'s own "the console must not
    /// report completion before the job has completed."
    /// </summary>
    Task<ConversationSummaryItem?> GetByIdAsync(ConversationId conversationId, SiteId siteId, CancellationToken cancellationToken);

    /// <summary>
    /// `18-07`: every other conversation this visitor has ever had, newest first - the read behind
    /// the console's returning-visitor-history panel. Keyset-paginated like
    /// <see cref="GetAllForSiteAsync"/>, for the identical reason: a visitor's own history only grows
    /// and carries no natural bound.
    ///
    /// <paramref name="excludeConversationId"/> is the conversation the operator is already looking
    /// at - always excluded, since a panel showing "this visitor's other conversations" that includes
    /// the one already on screen would be confusing rather than useful, and the caller
    /// (<c>GetVisitorHistoryHandler</c>) already knows exactly which id that is.
    /// </summary>
    Task<VisitorHistoryPage> GetVisitorHistoryAsync(
        VisitorId visitorId, ConversationId excludeConversationId, Guid? beforeId, int pageSize,
        CancellationToken cancellationToken);

    /// <summary>
    /// `23-06`: the second of the install screen's two facts - "the product was used", read from
    /// `conversations` rather than stored anywhere new (this item's own Scope: "nothing new is stored
    /// for it, which is the point: the data already exists and only the question is new"). <see
    /// langword="null"/> when this site has never had a conversation at all.
    ///
    /// <para><b>Why this returns a timestamp and not a bool already compared against a window.</b> The
    /// window (`SiteInstallationOptions.RecentlyThresholdDays`) is configuration the caller (
    /// <c>GetSiteInstallationHandler</c>) already holds and a moment (<c>IClock</c>) this read store has
    /// no business reading on its own (`clean-architecture.md`: no <c>DateTime.Now</c> outside
    /// Infrastructure, and this is a Dapper adapter, not the boundary that decides what "recent" means) -
    /// returning the raw fact and letting the caller apply its own threshold keeps this store answering
    /// only "what is true", never "what should we conclude".</para>
    ///
    /// <para><b>Why <c>ORDER BY id DESC LIMIT 1</c> rather than <c>MAX(created_at)</c> or a
    /// <c>WHERE created_at &gt;=</c> filter.</b> Conversation ids are UUID v7 (<see
    /// cref="IIdGenerator"/>), so id order already is creation order - <see cref="GetAllForSiteAsync"/>'s
    /// own remarks state this precedent first. That means this query is served by the existing
    /// `ix_conversations_site_all (site_id, id)` index with no new index to add: one index-only lookup
    /// for the newest row per site, not a sequential scan bounded by an unindexed `created_at`
    /// predicate.</para>
    /// </summary>
    Task<DateTimeOffset?> GetMostRecentCreatedAtAsync(SiteId siteId, CancellationToken cancellationToken);
    /// `24-11`: every conversation this visitor has ever had, oldest first, unpaginated - the read
    /// behind a visitor-scoped export. Deliberately not <see cref="GetVisitorHistoryAsync"/> with its
    /// exclusion and paging dropped: that method exists for an operator's own convenience panel and is
    /// gated by nothing this method needs to repeat (see <c>ExportVisitorHandler</c>'s own remarks on
    /// why an export's completeness question is not the same as that panel's authorization-scope
    /// question). Unbounded rather than keyset-paginated - the same "small and bounded, one person's
    /// own history" reasoning <see cref="IVisitorContactDetailRepository.GetForVisitorAsync"/> already
    /// gives for itself: nobody accumulates thousands of conversations, so a subject-access export can
    /// read every id in one round trip rather than paging through its own source data.
    ///
    /// <para>No separate <c>siteId</c> parameter - the same reasoning <see cref="GetVisitorHistoryAsync"/>
    /// already states for itself: a <see cref="Visitor"/>, and therefore every conversation hanging off
    /// one, belongs to exactly one site, and the caller only ever holds a <see cref="VisitorId"/> it
    /// already resolved from a conversation it proved belongs to the caller's own site.</para>
    /// </summary>
    Task<IReadOnlyList<ConversationId>> ListAllForVisitorAsync(VisitorId visitorId, CancellationToken cancellationToken);
}
