namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `12-02`: the read-side port behind `GET /api/v1/owner/sites` - hand-written SQL over the write
/// model, never through an aggregate (`adr/0004`), the same mechanism every other read model in this
/// codebase uses. Nothing new is introduced here; what is new is the *scope* of the one query it
/// declares.
///
/// <para><b>This is the only cross-tenant read in `ago-chat`.</b> Every other query in this codebase
/// is tenant-scoped by construction - it takes a <c>SiteId</c> (or a <c>ConversationId</c>/
/// <c>OperatorId</c> that belongs to exactly one site) and its `WHERE` clause cannot address another
/// tenant's rows even if a caller wanted it to. <see cref="ListSitesAsync"/> deliberately takes no
/// site parameter at all: "how many accounts exist and what is each one doing" has no answer inside
/// one tenant. Because the port itself is the boundary being crossed, it is stated here rather than
/// only at the endpoint.</para>
///
/// <para><b>Why that is safe.</b> One caller reaches this port -
/// <c>ListSitesForOwnerHandler</c>, resolved by exactly one endpoint, which carries
/// `12-01`'s `RequirePlatformOwner` policy (`adr/0032`). That policy is satisfied only by a
/// `platform-owner` <i>realm</i> role Keycloak itself signs into the token; no row this codebase can
/// write - not `5-08`'s site-wide `"Admin"` with `site:configure`, not any future
/// `roles`/`operator_roles` grant however broadly seeded - can satisfy it, because
/// <c>PlatformOwnerAuthorizationHandler</c> reads none of those tables. So the blast radius of this
/// port is exactly "whoever an administrator of the identity provider deliberately granted a realm
/// role to", and there is no second path in.</para>
///
/// <para><b>Not a caching concern</b> (`CLAUDE.md` rule 8, `caching.md`). Nothing this query returns
/// feeds a write, a compare-and-set, or a capacity check anywhere in the system: no cap is enforced
/// from these numbers (`12-02`'s Out of scope is explicit that enforcement is unbuilt), no assignment
/// decision reads them, nothing branches on them but a human's eyes. It is pure observability, the
/// same category `7-02`'s metrics occupy - so rule 8's "never cache what a write decision depends
/// on" does not bite here, and equally, nothing here needs a cache today: it is one query, run by one
/// person, at human frequency. Adding one would be inventing a hot path that does not exist.</para>
/// </summary>
public interface IPlatformOverviewReadStore
{
    /// <summary>
    /// One keyset page of sites, newest id first, each with its usage signals.
    ///
    /// <para><paramref name="recentMessagesSince"/> is the <b>bounded</b> lower bound for the message
    /// count and last-activity timestamp, and it is required rather than optional on purpose.
    /// `15-09`/`adr/0087`: `messages` is now `PARTITION BY HASH (site_id)`, not `RANGE (created_at)`
    /// (`2-06`'s original scheme, then `13-06`'s two-level one) - `created_at` carries no partition
    /// pruning power at all any more, so the reason this bound still matters changed. What prunes the
    /// bucket is `m.site_id = p.id`, correlated per row (`PlatformOverviewReadStore`'s own remarks on
    /// why that is the correct shape for a genuine cross-tenant read); `recentMessagesSince` is what
    /// keeps the scan *within* that one already-pruned bucket bounded to a recent window instead of the
    /// site's entire history, via the composite `(site_id, created_at)` index - an ordinary index-scan
    /// cost concern now, not a partition-pruning one, but the requirement not to make it optional is
    /// unchanged: an all-time `COUNT(*)` per site still grows without bound for the life of the
    /// deployment, on the one endpoint whose whole job is to stay cheap enough that an operator runs it
    /// casually. The window's *value* is the caller's policy decision (<c>ListSitesForOwnerHandler</c>,
    /// from <c>IClock</c>), not this port's: the SQL only needs it to be bounded, not to be any
    /// particular length.</para>
    ///
    /// <para><paramref name="before"/> <see langword="null"/> means "the first page" (the same
    /// convention <see cref="IConversationReadStore.GetAllForSiteAsync"/>'s `beforeId` uses).</para>
    /// </summary>
    Task<SiteOverviewPage> ListSitesAsync(
        DateTimeOffset recentMessagesSince, Guid? before, int limit, CancellationToken cancellationToken);
}
