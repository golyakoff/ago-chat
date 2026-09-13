using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `23-69`/`23-77`: the write and tenant-facing read for <c>visitor_restrictions</c> - one shared
/// mechanism for both items, decided in dialogue with the author 2026-09-13 (see either backlog item's
/// own "Answered" section for the full reasoning). Keyed <c>(SiteId, VisitorId)</c>, not
/// <c>ConversationId</c> - the entire reason this table exists rather than reusing `24-10`'s own
/// <see cref="IConversationBlockRepository"/>, whose key cannot see a visitor's *next* conversation.
///
/// <para><b>Raw Npgsql, no EF, no aggregate</b> - the same "a receipt with no business invariant beyond
/// one row per event" shape <see cref="IAccessRecordRepository"/>/<see cref="IErasureRequestRepository"/>
/// already establish, and the same reason: routing this through <see cref="IConversationRepository"/>
/// would mean loading a whole conversation aggregate to answer a question that is really about the
/// visitor, not any one conversation.</para>
///
/// <para><b>One port serves both the write and the tenant's own read-back</b>, mirroring
/// <see cref="IAccessRecordRepository"/> exactly (that interface's own remarks give the reasoning in
/// full): there is no aggregate here complex enough to earn a separate Dapper read store, and the one
/// console screen this item's own Done-when asks for ("the tenant can see how many, by whom, and read
/// the conversations themselves") is the only reader <see cref="ListForSiteAsync"/> exists to serve.
/// </para>
///
/// <para><b>Append-only, not one row per visitor.</b> <see cref="RestrictAsync"/> always inserts a new
/// row rather than upserting a current-state row keyed on <c>(SiteId, VisitorId)</c> alone - the same
/// "current state is a flag, history is an append-only log" split <see cref="ConversationBlockRecordKind"/>'s
/// own remarks describe for <c>conversation_block_records</c>, chosen here for the identical reason:
/// this item's own Done-when asks the tenant to see *how many* times a visitor was restricted and read
/// each occurrence's own source conversation, which a single overwritten row could not answer after a
/// second restriction replaced the first. No uniqueness constraint enforces "at most one active
/// restriction per visitor" the way `ix_conversations_one_open_per_visitor` does for conversations -
/// deliberately: two operators independently marking the same visitor spam and blocking them within the
/// same second is a low-frequency, human-triggered race (unlike the widget's own concurrent-tab race
/// that index exists for), and <see cref="IsActiveAsync"/>'s own <c>OR</c>-across-rows semantics stay
/// correct even if it happens - a small, harmless chance of two active rows costs nothing a database
/// constraint would be worth adding for.</para>
/// </summary>
public interface IVisitorRestrictionRepository
{
    /// <summary>Writes one new restriction row. Always succeeds (no "already restricted" outcome to
    /// report) - re-restricting an already-restricted visitor is not an error, it simply layers a new
    /// active record (see this interface's own remarks on why no uniqueness constraint prevents it).
    /// <paramref name="expiresAt"/> <see langword="null"/> means <see cref="VisitorRestrictionKind.Block"/>'s
    /// own indefinite shape; a real timestamp means <see cref="VisitorRestrictionKind.Spam"/>'s own
    /// time-windowed one - the caller decides which, this method does not validate the pairing.</summary>
    Task RestrictAsync(
        SiteId siteId,
        VisitorId visitorId,
        OperatorId restrictedBy,
        VisitorRestrictionKind kind,
        DateTimeOffset? expiresAt,
        ConversationId sourceConversationId,
        Guid recordId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>Lifts every currently-active restriction this visitor carries on this site (ordinarily
    /// zero or one row, per this interface's own remarks on why more than one is possible but rare) -
    /// stamps <c>lifted_at</c>/<c>lifted_by</c>, the same "an early reversal is a real act with its own
    /// actor" shape <see cref="IConversationBlockRepository.UnblockAsync"/> already gives its own
    /// reversal. Returns <see langword="false"/> when nothing was active to lift (the visitor was never
    /// restricted, or their restriction had already naturally expired or already been lifted) - the
    /// caller (<c>LiftVisitorRestrictionHandler</c>) turns that into
    /// <c>ConversationErrors.VisitorNotRestricted</c> rather than silently succeeding at nothing.
    /// </summary>
    Task<bool> LiftAsync(
        SiteId siteId, VisitorId visitorId, OperatorId liftedBy, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The enforcement read - <c>StartConversationHandler</c>'s own gate, alongside its
    /// existing <c>GetActiveForVisitorAsync</c> read (both items' own "Answered" sections). A row counts
    /// as active when it has not been lifted and either carries no expiry or has not yet reached it -
    /// evaluated fresh against <paramref name="now"/> on every call, so a time-windowed mute lifts
    /// itself the instant it expires with no background job needed to notice (both items' own Done-when:
    /// "after the expiry, the restriction lifts on its own").</summary>
    Task<bool> IsActiveAsync(SiteId siteId, VisitorId visitorId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The one active restriction's own kind, if any - <c>LiftVisitorRestrictionHandler</c>'s
    /// own read, used only to decide which permission a lift requires (<see cref="VisitorRestrictionKind.Spam"/>
    /// needs <c>Permission.ConversationMarkSpam</c>, <see cref="VisitorRestrictionKind.Block"/> needs
    /// <c>Permission.ConversationBlock</c> - that handler's own remarks). When more than one row is
    /// active at once (this interface's own remarks on why that is possible but rare), the more
    /// permissive kind to lift wins - practically: if either active row is <see cref="VisitorRestrictionKind.Block"/>,
    /// this returns that, since lifting must clear every active row regardless of kind and the caller
    /// holding the Admin-only permission can always also lift a mute.</summary>
    Task<VisitorRestrictionKind?> GetActiveKindAsync(
        SiteId siteId, VisitorId visitorId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The tenant's own read-back - both items' own Done-when: "the tenant can see how many, by
    /// whom" (`23-69`) and a reversible, recorded, site-scoped act the tenant can review (`23-77`).
    /// Keyset by <c>id</c> descending (newest first), the same convention
    /// <see cref="IAccessRecordRepository.ListForSiteAsync"/> already uses; <paramref name="beforeId"/>
    /// <see langword="null"/> means the first page.</summary>
    Task<VisitorRestrictionPage> ListForSiteAsync(
        SiteId siteId, Guid? beforeId, int limit, CancellationToken cancellationToken);
}

/// <summary>One <c>visitor_restrictions</c> row, read back for the tenant's own report - every column
/// <see cref="IVisitorRestrictionRepository.RestrictAsync"/> wrote, plus whether and by whom it was
/// later lifted.</summary>
public sealed record VisitorRestrictionItem(
    Guid Id,
    VisitorId VisitorId,
    VisitorRestrictionKind Kind,
    DateTimeOffset RestrictedAt,
    OperatorId RestrictedBy,
    DateTimeOffset? ExpiresAt,
    ConversationId SourceConversationId,
    DateTimeOffset? LiftedAt,
    OperatorId? LiftedBy);

/// <summary>One keyset page of <see cref="IVisitorRestrictionRepository.ListForSiteAsync"/> - the same
/// shape every other keyset read in this codebase returns (<c>NextBeforeId</c> <see langword="null"/>
/// once the oldest row has been reached).</summary>
public sealed record VisitorRestrictionPage(IReadOnlyList<VisitorRestrictionItem> Items, Guid? NextBeforeId);
