using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `8-07`: the three questions a demo tenant's lifecycle asks that no existing port answers - how many
/// are alive, which have expired, and remove one completely.
///
/// <para>Its own port rather than methods on <see cref="ISiteRepository"/>, for the reason
/// <see cref="ISiteRegistrationRepository"/> gives for existing at all: <see cref="DeleteAsync"/>
/// deliberately reaches rows belonging to other aggregates, and a port that does that should say so in
/// its name rather than widening a single-aggregate port's contract.</para>
/// </summary>
public interface IDemoTenantRepository
{
    /// <summary>
    /// How many demo tenants exist and have not yet expired - the number the total cap is checked
    /// against.
    ///
    /// <para><b>Counted, not cached</b> (CLAUDE.md rule 8): the cap is a write decision, so it reads
    /// from the database inside the request that acts on it. A cached count is a cap that can be
    /// exceeded by exactly the traffic it exists to stop.</para>
    /// </summary>
    Task<int> CountLiveAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Demo tenants whose window has passed, oldest first, bounded by
    /// <paramref name="limit"/> - the same bounded-batch shape `15-04`/`AttachmentOrphanSweepJob`
    /// already established, so one sweep can never turn into an unbounded delete.</summary>
    Task<IReadOnlyList<ExpiredDemoTenant>> ListExpiredAsync(
        DateTimeOffset now, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the site row. <b>What that reaches, and what it does not, is the whole of
    /// `8-07`'s Done-when #3</b> - see the implementation's own remarks, and `adr/0058`. This method
    /// removes Postgres rows only; the caller is responsible for the object store and the identity
    /// provider, because both can fail independently and neither can join this transaction.
    /// </summary>
    Task DeleteSiteAsync(SiteId siteId, CancellationToken cancellationToken);

    /// <summary>Every attachment object key belonging to this site, so the caller can remove the bytes
    /// before the rows that point at them disappear. Ordering matters: after
    /// <see cref="DeleteSiteAsync"/> there is nothing left to enumerate, and the objects would be
    /// orphaned in MinIO forever.</summary>
    Task<IReadOnlyList<string>> ListAttachmentObjectKeysAsync(SiteId siteId, CancellationToken cancellationToken);
}

/// <summary>One expired demo tenant, and the two identifiers its removal needs outside Postgres: the
/// site whose rows go, and the Keycloak subjects whose users go with them.</summary>
public sealed record ExpiredDemoTenant(
    SiteId SiteId, string PublicKey, DateTimeOffset ExpiredAt, IReadOnlyList<string> ExternalSubjectIds);
