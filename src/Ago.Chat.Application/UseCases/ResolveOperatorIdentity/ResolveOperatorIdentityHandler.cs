using Ago.Chat.Application.Abstractions;

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
/// <c>(sub, RequestedSiteId)</c> specifically. Found -&gt; return it. <b>Not found -&gt; return
/// <see langword="null"/>, never fall back to a different one of this identity's tenancies.</b> This
/// is the one invariant the whole design leans on (`adr/0068`'s own "Negative consequences"
/// paragraph): a client-controlled header/query-string value must never *widen* what a request
/// resolves to, only *select among* rows already proven to belong to this `sub` by the database
/// query itself.</item>
/// <item><b>Absent:</b> fetch every row for this `sub`.
/// <list type="bullet">
/// <item>Zero -&gt; <see langword="null"/> (unchanged from before this item).</item>
/// <item>Exactly one -&gt; return it - byte-for-byte the same result this handler already produced
/// for every operator that existed before `13-07`, which is what makes this the regression case
/// proven, not assumed, by <c>ResolveOperatorIdentityHandlerTests</c>.</item>
/// <item><b>More than one -&gt; <see langword="null"/>.</b> Impossible before this item (the old
/// global-unique index made it so); an identity with several tenancies and no requested-site signal
/// is, from this resolver's point of view, exactly as unresolved as one with none - guessing which
/// tenancy to use would be the same cross-tenant misdirection the first bullet refuses. The console
/// is responsible for always supplying a requested site once it knows an identity has more than
/// one tenancy (`PermissionsProvider`, `ago-console`).</item>
/// </list>
/// </item>
/// </list>
/// </para>
/// </summary>
public sealed class ResolveOperatorIdentityHandler(IOperatorRepository operators)
{
    public async Task<OperatorIdentity?> HandleAsync(ResolveOperatorIdentityQuery query, CancellationToken cancellationToken)
    {
        if (query.RequestedSiteId is { } requestedSiteId)
        {
            var requested = await operators.GetByExternalSubjectIdAndSiteIdAsync(
                query.ExternalSubjectId, requestedSiteId, cancellationToken);
            return requested is null ? null : new OperatorIdentity(requested.Id, requested.SiteId);
        }

        var tenancies = await operators.ListByExternalSubjectIdAsync(query.ExternalSubjectId, cancellationToken);
        if (tenancies.Count != 1)
        {
            // Zero -> unresolved, unchanged from before this item. More than one -> also unresolved -
            // new as of `13-07`, and deliberately not "pick the first" (this handler's own doc
            // comment, case 2's third bullet).
            return null;
        }

        var operatorEntity = tenancies[0];
        return new OperatorIdentity(operatorEntity.Id, operatorEntity.SiteId);
    }
}
