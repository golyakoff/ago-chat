using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.UseCases.ListMyTenancies;

/// <summary>
/// `13-07`/`adr/0068`: backs `GET /api/v1/me/tenancies` - gated only by `RequireKeycloakIdentity`
/// (`Program.cs`, the same policy `RegisterSiteHandler`'s own bootstrap endpoint uses, for the
/// identical reason: an identity with zero or several tenancies cannot satisfy
/// `RequireOperatorIdentity`, since that policy needs an already-resolved `OperatorId` claim - see
/// `ResolveOperatorIdentityHandler`'s own doc comment for why "more than one, no site requested" is
/// unresolved by design). This is the one place in this codebase that composes
/// <see cref="IOperatorRepository"/> and <see cref="ISiteRepository"/> directly in a handler rather
/// than through a dedicated join port, the same pattern <c>GetSiteConfigByPublicKeyHandler</c> and
/// its siblings already use for their own single-aggregate reads - <c>IPlatformOverviewReadStore</c>
/// (`12-02`) is a *different* shape, a dedicated read-store built for a paginated, multi-column
/// cross-tenant aggregate query; this read is a handful of rows for one identity's own tenancies, and
/// a second dedicated read-store for it would be exactly the premature generalisation
/// `clean-architecture.md` warns against for a caller this small.
///
/// <para>N+1 by construction: one <see cref="IOperatorRepository.ListByExternalSubjectIdAsync"/> call,
/// then one <see cref="ISiteRepository.GetByIdAsync"/> per tenancy. Deliberate, not overlooked - the
/// realistic size of this list is "how many `Site`s can one person plausibly administer", not a page
/// of hundreds, so there is no batched-lookup port anywhere in this codebase to reuse
/// (<see cref="ISiteRepository"/>'s own shape has no "get many by id"), and adding one for a caller
/// that runs once per console session, for a handful of rows, would be the same premature
/// generalisation this codebase avoids elsewhere.</para>
/// </summary>
public sealed class ListMyTenanciesHandler(IOperatorRepository operators, ISiteRepository sites)
{
    public async Task<IReadOnlyList<Tenancy>> HandleAsync(ListMyTenanciesQuery query, CancellationToken cancellationToken)
    {
        var operatorRows = await operators.ListByExternalSubjectIdAsync(query.ExternalSubjectId, cancellationToken);
        if (operatorRows.Count == 0)
        {
            return [];
        }

        var tenancies = new List<Tenancy>(operatorRows.Count);
        foreach (var operatorRow in operatorRows)
        {
            var site = await sites.GetByIdAsync(operatorRow.SiteId, cancellationToken);
            if (site is null)
            {
                // Should not happen - `operators.site_id` carries a foreign key onto `sites`
                // (`OperatorConfiguration.HasOne<Site>()`) - but a read handler answering "which
                // sites can you switch to" must not throw on a row it cannot fully describe; it
                // simply is not offered as a switchable tenancy.
                continue;
            }

            tenancies.Add(new Tenancy(site.Id.Value, site.Name));
        }

        // Ordered by name, ordinal-case-insensitive - the simplest stable order for a switcher list
        // a human reads, and the one the backlog item's own Scope asked to have stated once chosen.
        tenancies.Sort((a, b) => string.Compare(a.SiteName, b.SiteName, StringComparison.OrdinalIgnoreCase));
        return tenancies;
    }
}
