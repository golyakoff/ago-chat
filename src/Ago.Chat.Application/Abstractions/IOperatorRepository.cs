using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `5-05`: shaped around the one thing that needs it - resolving a validated OIDC principal back to
/// an operator (`adr/0022`) - not a general operator CRUD port. Grow this only when a second real
/// caller needs a different question answered.
/// </summary>
public interface IOperatorRepository
{
    /// <summary>
    /// `13-07`/`adr/0068`: the `RequestedSiteId`-present path of `ResolveOperatorIdentityHandler`'s
    /// resolution algorithm - the *only* row that may ever answer a request carrying an explicit
    /// active-site signal. Returns <see langword="null"/> when this identity holds no `operators` row
    /// for <paramref name="siteId"/> specifically, even if it holds one for a different site - the
    /// caller must never fall back to a different tenancy on a miss (`adr/0068`'s own "never
    /// misdirect" invariant, `tenant-isolation.md`'s worst-case failure mode).
    ///
    /// <para><b>`13-03`: only a row with <see cref="Operator.HoldsSeat"/> and no
    /// <see cref="Operator.RemovedAt"/> is ever returned.</b> This is the mechanism behind
    /// `Ago.Chat.Api.Auth.OperatorIdentityClaimsTransformation`'s own sign-in-blocking behaviour - a
    /// seat-less or removed operator resolves to no <see cref="Operator"/> here, which
    /// `ResolveOperatorIdentityHandler` already turns into "no `OperatorId` claim added", the exact same
    /// shape as no row existing at all (`decisions/0006`'s "only the owner and as many operators as are
    /// paid for can sign in"). No new policy code needed anywhere above this query - the same "the query
    /// itself is the source of truth" discipline `adr/0068`'s own remarks already establish for the
    /// `RequestedSiteId` case.</para>
    /// </summary>
    Task<Operator?> GetByExternalSubjectIdAndSiteIdAsync(string externalSubjectId, SiteId siteId, CancellationToken cancellationToken);

    /// <summary>
    /// `13-07`/`adr/0068`: every `operators` row for this identity, across every `Site` it
    /// administers - the `RequestedSiteId`-absent path. Before this item, `external_subject_id` was
    /// globally unique, so this list could only ever hold zero or one row; the composite unique index
    /// on `(external_subject_id, site_id)` this item introduces
    /// (<c>OperatorConfiguration</c>) is what makes more than one a real, expected shape. Zero, one,
    /// or "more than one with no site requested" are three genuinely different answers -
    /// <see cref="ResolveOperatorIdentityHandler"/> is where that distinction is made, never here;
    /// this method's only job is to return every row, honestly.
    ///
    /// <para>`13-03`: "every row" here means every row this identity may still sign in with - the same
    /// <see cref="Operator.HoldsSeat"/>/<see cref="Operator.RemovedAt"/> filter
    /// <see cref="GetByExternalSubjectIdAndSiteIdAsync"/>'s own remarks describe, for the identical
    /// reason: a tenancy this identity administers but cannot currently sign in to is not a real answer
    /// to "which site should this token resolve to".</para>
    /// </summary>
    Task<IReadOnlyList<Operator>> ListByExternalSubjectIdAsync(string externalSubjectId, CancellationToken cancellationToken);

    /// <summary>
    /// `14-04`: is *anybody* on duty for this site right now - the one question the offline
    /// auto-reply's own precondition asks, and nothing wider. Shaped around the caller exactly the way
    /// <c>ISiteRepository.AnyAllowsOriginAsync</c> already is: returning the operators instead would
    /// hand the caller a list it has no use for and invite a second, different capacity judgment to
    /// grow next to the assignment engine's.
    ///
    /// <para><b>Deliberately weaker than the assignment engine's own candidate query.</b>
    /// <c>SkipLockedAssignmentClaimer</c> looks for an <c>Online</c> operator <em>with room</em>
    /// (<c>active_chats &lt; capacity</c>); this asks only whether one is <c>Online</c>. The
    /// difference is the whole point: an online operator who is momentarily full is a human being who
    /// will get to this conversation, and telling that visitor "nobody is here" would be false and
    /// would land seconds before a real answer. "Every operator is busy" is a queue-wait, not an
    /// absence.</para>
    ///
    /// <para>Read from Postgres, never from Redis presence: this decides whether to write a message,
    /// so it is a write decision, and <c>CLAUDE.md</c> rule 8 puts it in the database. The
    /// <c>operators.status</c> column is also the same input the assignment engine reads, so the two
    /// cannot disagree about who is on duty.</para>
    /// </summary>
    Task<bool> AnyOnlineForSiteAsync(SiteId siteId, CancellationToken cancellationToken);

    /// <summary>`4-06`: the by-id lookup `SetOperatorPresenceHandler` needs - the two existing
    /// lookups are keyed by external identity or site, never by an operator's own id, because nothing
    /// before this needed "the operator who owns this hub connection", only "the operator this
    /// external principal resolves to".</summary>
    Task<Operator?> GetByIdAsync(OperatorId id, CancellationToken cancellationToken);

    /// <summary>`13-03`: the same by-id lookup, scoped to a site - `ToggleOperatorSeatHandler`/
    /// `RemoveOperatorHandler` both act on an operator named by a site's own administrator, and both
    /// must refuse an id that resolves to a real operator on a *different* site rather than silently
    /// finding it anyway (the identical cross-tenant-misdirection concern `adr/0068`'s own remarks name
    /// for the resolution path above). <see langword="null"/> for either "no such operator" or "that
    /// operator belongs to a different site" - deliberately the same answer for both, so a caller
    /// cannot use this method to probe whether an id exists at all.</summary>
    Task<Operator?> GetByIdAsync(OperatorId id, SiteId siteId, CancellationToken cancellationToken);

    /// <summary>`13-03`: the over-seats condition's own read -
    /// `count(operators where HoldsSeat AND RemovedAt IS NULL)` for one site. A derived, read-time
    /// count, not a stored counter (this item's own Scope: "computed at read time, not a stored flag") -
    /// `GetSeatAssignmentSummaryHandler`'s only caller, and `ToggleOperatorSeatHandler`'s own capacity
    /// guard before assigning one more seat.</summary>
    Task<int> CountHeldSeatsAsync(SiteId siteId, CancellationToken cancellationToken);

    /// <summary>Persists an <see cref="Operator"/> mutated via <see cref="Operator.GoOnline"/>/
    /// <see cref="Operator.GoOffline"/>/<see cref="Operator.ToggleSeat"/>/<see cref="Operator.Remove"/>.
    /// Always called on an entity this same request already loaded through one of the
    /// <see cref="GetByIdAsync(OperatorId,System.Threading.CancellationToken)"/> overloads, so EF's
    /// change tracking is what actually writes the row - no concurrency token on this table
    /// (`OperatorConfiguration`), so unlike `IConversationRepository.SaveAsync` there is nothing here to
    /// retry.</summary>
    Task SaveAsync(Operator operatorEntity, CancellationToken cancellationToken);

    /// <summary>
    /// `23-02`: the sign-in refresh `decisions.md` §1 requires - "rewritten at every sign-in" - wired
    /// at the one point a sign-in is actually observable (`GetMyPermissionsHandler`,
    /// `GET /api/v1/operators/me`). A conditional `UPDATE`, never a load-mutate-save through the
    /// aggregate: <see cref="Operator.DisplayName"/>/<see cref="Operator.Email"/> have no invariant to
    /// enforce, the same "no invariant, no reason to load the aggregate" reasoning
    /// <c>OperatorCapacityStore</c>'s own `active_chats` compare-and-set already established for a
    /// different column on this same table. Returns <see langword="true"/> only when a row was
    /// actually written - the caller's own claims matched what was already stored costs one statement
    /// and no write, and that fact is observable here rather than requiring a caller to compare values
    /// by eye.
    /// </summary>
    Task<bool> RefreshIdentityAsync(
        OperatorId operatorId, string? displayName, string? email, CancellationToken cancellationToken);
}
