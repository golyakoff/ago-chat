namespace Ago.Chat.Contracts;

/// <summary>
/// `24-17`: `GET /api/v1/owner/tenant-isolation`'s response body - the five headline figures
/// `docs/architecture/tenant-isolation.md` states by hand, computed instead from the assembly and the
/// route table that are actually running.
///
/// <para><b>Why this is not a page of `TenantScopeExemptions`' own reasons.</b> Those are prose
/// written for a reviewer reading source, not a wire payload a console renders - and this endpoint is
/// already the narrowest possible audience (platform owner only). What crosses the wire is exactly
/// what the item's own Scope calls for: the counts, and the two lists that mark a *finding* rather
/// than a documentation drift (see <see cref="UnaccountedKeys"/>).</para>
/// </summary>
/// <param name="EntryPoints">Every public method of every <c>*Handler</c> class in
/// <c>Ago.Chat.Application.UseCases</c>.</param>
/// <param name="HandlerClasses">How many distinct handler classes those entry points come from.</param>
/// <param name="RbacGated">Entry points that take a <c>SiteId</c> and call <c>IPermissionChecker</c>.</param>
/// <param name="ExemptListed">Entry points listed in <c>TenantScopeExemptions</c> with a stated
/// reason.</param>
/// <param name="UnaccountedKeys">Entry points that are neither <paramref name="RbacGated"/> nor
/// <paramref name="ExemptListed"/>. <b>This is the field that must never read as "just a number that
/// moved".</b> A non-empty list here means a use case exists today that the build-time
/// <c>TenantScopeTests</c> would already fail the build over - so it can only mean the running
/// deployment is ahead of the last green build, never a gap that guard would have missed.</param>
/// <param name="ExemptButAlsoLooksGated">Exemption entries whose own handler now also satisfies the
/// gated shape - a stale exemption entry, not a new gap.</param>
/// <param name="RoutesAndHubMethods">HTTP routes (excluding `GET /healthz/version`, which carries no
/// tenant data) plus SignalR hub methods on <c>OperatorHub</c>/<c>VisitorHub</c>, read from this
/// process's own live route table and hub types rather than re-derived from source text.</param>
/// <param name="ClientSuppliedSiteIdRoutes">Of those, how many resolve to a path containing a literal
/// <c>{siteId}</c> route segment - the routes where the permission check is the entire tenant-isolation
/// defence, per `docs/architecture/tenant-isolation.md`'s own category 3.</param>
/// <param name="GeneratedAtUtc">When this snapshot was computed - always "just now": nothing here is
/// ever cached (see <c>TenantScopeInspector</c>'s own remarks on why), but a console rendering this
/// still benefits from being told so explicitly rather than assuming it.</param>
public sealed record TenantIsolationSummaryResponse(
    int EntryPoints,
    int HandlerClasses,
    int RbacGated,
    int ExemptListed,
    IReadOnlyList<string> UnaccountedKeys,
    IReadOnlyList<string> ExemptButAlsoLooksGated,
    int RoutesAndHubMethods,
    int ClientSuppliedSiteIdRoutes,
    DateTimeOffset GeneratedAtUtc);
