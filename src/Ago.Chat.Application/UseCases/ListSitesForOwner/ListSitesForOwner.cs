namespace Ago.Chat.Application.UseCases.ListSitesForOwner;

/// <summary>
/// `12-02`: the platform owner's cross-tenant overview query. <paramref name="Before"/>
/// <see langword="null"/> means "the first page" - the same convention every other keyset read in
/// this codebase uses (`GetAllConversationsForSite.BeforeId`,
/// `GetConversationHistoryAsOperator.BeforeSequence`).
///
/// <para><b>No `RequestedBy` field</b>, unlike every other query record here. Those carry an
/// <c>OperatorId</c> because their handler re-checks a permission against the `roles`/
/// `operator_roles` tables (`GetAllConversationsForSiteHandler`). The platform owner is deliberately
/// not expressible in those tables at all (`adr/0032`): the fact that authorizes this call is a
/// Keycloak realm role on the token, decided by `12-01`'s `RequirePlatformOwner` policy at the
/// endpoint. Carrying an id here would suggest a check this layer cannot make and does not
/// make - see <see cref="ListSitesForOwnerHandler"/> on why that is stated rather than papered over
/// with a field nothing reads.</para>
///
/// <para><b>No sort parameter</b> - `12-02` named one as an explicit nice-to-have ("if trivial to add
/// alongside the same query"), and it is not trivial, so it is not here. Sorting by seat count or
/// recent message volume means ordering by a computed aggregate, and this endpoint's pagination is
/// keyset (`data-model.md` bans `OFFSET`): a keyset cursor has to be a stored, unique, indexed column
/// the `WHERE` clause can resume from, which a per-request aggregate is not. Delivering it properly
/// needs either a composite cursor over (aggregate, id) with the aggregate recomputed identically on
/// every page - correct but a genuinely different query - or `OFFSET`, which is banned. A caller that
/// wants "the ten biggest tenants" is asking for a different, ranked query; that is real work for
/// whoever needs it, not a parameter to bolt onto this one.</para>
/// </summary>
public sealed record ListSitesForOwner(Guid? Before, int? Limit);
