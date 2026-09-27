using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `26-237`/`adr/0186` §8.1: the application-layer label resolver the analytics reports use to turn the
/// <b>id-keyed</b> rows the <c>ago_analytics</c> rollups return into the display names the console shows.
///
/// <para><b>Why this port exists at all.</b> The rollups are keyed by id (operator id, tag id) and carry
/// <em>no</em> display name, on purpose: resolving a name would be a cross-database join from
/// <c>ago_analytics</c> back to <c>ago_chat</c>, which `adr/0186` §3.2/§8.1 forbids because it would weld
/// the two databases to one host and defeat the rollup store's independent relocatability. The design's
/// answer is to resolve labels <em>in the application layer</em> - an in-memory merge across two read
/// ports, never a SQL join across two databases. This port is that second read: a small, batched lookup
/// of names from the operational <c>ago_chat</c> store, merged onto the rollup rows by the report
/// handlers. Before this port, the site-analytics report recovered operator names as a side effect of the
/// still-live operator-load report; moving every report off the live path (this epic) removes that
/// source, so the names must come from somewhere explicit - here.</para>
///
/// <para><b>Site-scoped, read-only, human-frequency.</b> Both lookups are scoped to one site (operators
/// and tags are site-owned) and feed only what a person reads on a report screen - no write decision
/// depends on them (rule 8). An id with no matching row (or, for an operator, a null display name that
/// predates `23-02`) is simply absent from the returned map; the caller keeps whatever fallback it had.</para>
/// </summary>
public interface IAnalyticsLabelReadStore
{
    /// <summary>The display names of the given operators on <paramref name="siteId"/>, keyed by id.
    /// Operators with no row, or a null display name, are omitted rather than mapped to null - so a caller
    /// can treat "present in the map" as "has a real name". An empty <paramref name="operatorIds"/>
    /// returns an empty map without touching the database.</summary>
    Task<IReadOnlyDictionary<OperatorId, string>> GetOperatorDisplayNamesAsync(
        SiteId siteId, IReadOnlyCollection<OperatorId> operatorIds, CancellationToken cancellationToken);

    /// <summary>The names of the given tags on <paramref name="siteId"/>, keyed by id. A tag with no row
    /// (e.g. deleted after the rollup was built) is omitted. An empty <paramref name="tagIds"/> returns an
    /// empty map without touching the database.</summary>
    Task<IReadOnlyDictionary<TagId, string>> GetTagNamesAsync(
        SiteId siteId, IReadOnlyCollection<TagId> tagIds, CancellationToken cancellationToken);
}
