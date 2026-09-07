using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.ResolveOperatorIdentity;

/// <summary>
/// `5-05`: the one lookup `Ago.Chat.Api`'s `IClaimsTransformation` needs, called once per request for
/// a validated Keycloak token - `adr/0022`'s own "not cached" call, since `PermissionChecker` already
/// pays an equivalent per-request database read on the same path and this is not a new order of
/// magnitude.
///
/// <para><b>`13-07`/`adr/0068`: the exact resolution algorithm, and why it is written this way.</b>
/// <list type="number">
/// <item><b><see cref="ResolveOperatorIdentityQuery.RequestedSiteId"/> is present:</b> look up
/// <c>(sub, RequestedSiteId)</c> specifically. Found, and eligible to sign in
/// (<see cref="OperatorSignInEligibility"/>, below) -&gt; return it. <b>Not found, or found but
/// ineligible -&gt; return <see langword="null"/>, never fall back to a different one of this
/// identity's tenancies.</b> This is the one invariant the whole design leans on (`adr/0068`'s own
/// "Negative consequences" paragraph): a client-controlled header/query-string value must never
/// *widen* what a request resolves to, only *select among* rows already proven to belong to this
/// `sub` by the database query itself.</item>
/// <item><b>Absent:</b> fetch every row for this `sub`, then keep only the ones eligible to sign in.
/// <list type="bullet">
/// <item>Zero eligible -&gt; <see langword="null"/> (unchanged from before this item).</item>
/// <item>Exactly one eligible -&gt; return it - byte-for-byte the same result this handler already
/// produced for every operator that existed before `13-07`, which is what makes this the regression
/// case proven, not assumed, by <c>ResolveOperatorIdentityHandlerTests</c>.</item>
/// <item><b>More than one eligible -&gt; <see langword="null"/>.</b> Impossible before `13-07` (the
/// old global-unique index made it so); an identity with several eligible tenancies and no
/// requested-site signal is, from this resolver's point of view, exactly as unresolved as one with
/// none - guessing which tenancy to use would be the same cross-tenant misdirection the first bullet
/// refuses. The console is responsible for always supplying a requested site once it knows an
/// identity has more than one tenancy (`PermissionsProvider`, `ago-console`).</item>
/// </list>
/// </item>
/// </list>
/// </para>
///
/// <para><b>`23-71`: "resolves to a row" and "may sign in" are no longer the same question.</b>
/// <see cref="IOperatorRepository.GetByExternalSubjectIdAndSiteIdAsync"/>/
/// <see cref="IOperatorRepository.ListByExternalSubjectIdAsync"/> answer the first (does a real,
/// non-removed `operators` row exist) - this handler is where the second is decided, via
/// <see cref="OperatorSignInEligibility.CanSignInAsync"/>: a row that holds no seat still resolves,
/// provided it holds this site's own <see cref="Permission.SiteManageOperators"/>
/// (`decisions/0006`'s "the owner and as many operators as are paid for", restored - the owner is
/// additional to the paid seats, not one of them). This is deliberately still only a sign-in
/// decision: the `operator_id` claim this resolution ultimately produces (`Ago.Chat.Api.Auth.OperatorIdentityClaimsTransformation`)
/// must no longer be read anywhere as "therefore routable" - see this item's own commit-prep report
/// for the full list of call sites re-read for that conflation, and `IOperatorRepository.AnyOnlineForSiteAsync`/
/// `SkipLockedAssignmentClaimer`/`RedisLockAssignmentClaimer`/`AssignConversationHandler` for where
/// <see cref="Operator.HoldsSeat"/> alone, never this claim's mere presence, keeps deciding
/// it.</para>
/// </summary>
public sealed class ResolveOperatorIdentityHandler(IOperatorRepository operators, IPermissionChecker permissions)
{
    public async Task<OperatorIdentity?> HandleAsync(ResolveOperatorIdentityQuery query, CancellationToken cancellationToken)
    {
        if (query.RequestedSiteId is { } requestedSiteId)
        {
            var requested = await operators.GetByExternalSubjectIdAndSiteIdAsync(
                query.ExternalSubjectId, requestedSiteId, cancellationToken);
            if (requested is null || !await OperatorSignInEligibility.CanSignInAsync(requested, permissions, cancellationToken))
            {
                return null;
            }

            return new OperatorIdentity(requested.Id, requested.SiteId);
        }

        var tenancies = await operators.ListByExternalSubjectIdAsync(query.ExternalSubjectId, cancellationToken);
        var eligible = new List<Operator>(tenancies.Count);
        foreach (var candidate in tenancies)
        {
            if (await OperatorSignInEligibility.CanSignInAsync(candidate, permissions, cancellationToken))
            {
                eligible.Add(candidate);
            }
        }

        if (eligible.Count != 1)
        {
            // Zero -> unresolved, unchanged from before this item. More than one -> also unresolved -
            // `13-07`, and deliberately not "pick the first" (this handler's own doc comment, case
            // 2's third bullet).
            return null;
        }

        var operatorEntity = eligible[0];
        return new OperatorIdentity(operatorEntity.Id, operatorEntity.SiteId);
    }
}
