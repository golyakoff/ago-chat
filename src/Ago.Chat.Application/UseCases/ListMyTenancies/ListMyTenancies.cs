namespace Ago.Chat.Application.UseCases.ListMyTenancies;

// Not named `ListMyTenancies` (matching the folder/namespace) - the identical ambiguity
// `ResolveOperatorIdentityQuery`'s own remarks describe: a type sharing its containing namespace's
// leaf segment name cannot be referenced unqualified from within that same namespace, which this
// use case's own test file needs to do.
/// <summary>
/// `13-07`/`adr/0068`: the calling identity's own read - "every `Site` this `sub` administers".
/// <see cref="ExternalSubjectId"/> comes from the caller's validated Keycloak token (`sub`), never
/// from the request, the same rule <see cref="Ago.Chat.Application.UseCases.RegisterSite.RegisterSite"/>
/// already follows and for the identical reason: identity is a property of the authenticated caller.
///
/// <para>Sibling of <see cref="Ago.Chat.Application.UseCases.ResolveOperatorIdentity.ResolveOperatorIdentityQuery"/>
/// on purpose - the ADR's own text calls this "the same query with the LIMIT/uniqueness assumption
/// removed": both ask "which `operators` rows exist for this `sub`", the resolver picks at most one
/// under its own rule, this one returns every row, unfiltered, for the console's switcher to render.</para>
/// </summary>
public sealed record ListMyTenanciesQuery(string ExternalSubjectId);

/// <summary>One tenancy as the console's switcher needs it - just enough to label and select it.
/// <see cref="SiteName"/>, not the full <c>Site</c> aggregate: this read has no business handing the
/// caller anything beyond what a switcher displays, the same "shaped around the one real caller"
/// discipline <see cref="Ago.Chat.Application.Abstractions.IOperatorRepository"/>'s own doc comment
/// states.</summary>
public sealed record Tenancy(Guid SiteId, string SiteName);
