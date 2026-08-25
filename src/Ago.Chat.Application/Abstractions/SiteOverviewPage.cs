namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `12-02`: a keyset page over <b>every</b> site, newest-first by id
/// (`data-model.md`: `OFFSET` is banned - the same reasoning
/// <see cref="ConversationListPage"/> applies to one site's conversations applies here to the tenant
/// list itself, which also only grows). <see cref="NextBefore"/> is <see langword="null"/> once the
/// caller has reached the oldest site.
///
/// <para>The cursor is a site id, not `created_at`, even though this page also carries a creation
/// time: `sites.created_at` is nullable and never backfilled (`Site.CreatedAt`), so it is not a total
/// order and cannot be a sort key without either inventing values for the rows that lack one or
/// bolting a `NULLS LAST` tiebreak onto a cursor that then cannot express "resume after the nulls".
/// Site ids are uuid v7 for every site this system creates (`IIdGenerator`, `10-02`'s
/// `RegisterSiteHandler`), so id order already is creation order for those - the identical argument
/// <see cref="ConversationListPage"/> makes for conversation ids.</para>
/// </summary>
public sealed record SiteOverviewPage(IReadOnlyList<SiteOverviewItem> Sites, Guid? NextBefore);
