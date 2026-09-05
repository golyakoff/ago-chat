using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `23-06`: the two facts a tenant's install screen is built from, plus the read that hands them
/// back - `first_seen_at`/`last_seen_at`/`last_refused_origin`/`last_refused_origin_at`, four
/// additive columns on `sites`.
///
/// <para><b>Its own port, not a method on <see cref="ISiteRepository"/> - the identical shape
/// <see cref="IErasureRequestRepository"/> already established, for the identical reason (see that
/// interface's own remarks).</b> <see cref="ISiteRepository.GetByIdAsync"/> loads the whole <see
/// cref="Site"/> aggregate and <see cref="ISiteRepository.SaveAsync"/> saves it back through EF's
/// change tracker under no concurrency-conflict story of its own. Routing a sighting through that path
/// would mean a full aggregate load-mutate-save on `POST /api/v1/visitor-sessions` - the hottest,
/// highest-concurrency write path in the product (`AuthEndpoints.HandleVisitorSessionAsync`) - to move
/// one timestamp forward. This port's writes are raw conditional `UPDATE`s instead, exactly the shape
/// `SiteConfiguration.cs`'s own remarks on `ErasureRequestedAt` describe: "never through Site's own
/// load-mutate-SaveChangesAsync path."</para>
///
/// <para><b>Why the read lives here too, rather than as a fifth field EF maps onto <see
/// cref="Site"/>.</b> The four columns are shadow properties (`SiteConfiguration.cs`), so nothing on
/// <see cref="Site"/> itself can expose them - <see cref="GetAsync"/> is this port's own read,
/// answering exactly the question <c>GetSiteInstallationHandler</c> needs and no more, the same
/// "the port answers the one real question a caller has" shape the write side already takes.</para>
/// </summary>
public interface ISiteInstallationSignalRepository
{
    /// <summary>
    /// `23-06`'s own scope: "at most one row write per site per minute" -
    /// <c>UPDATE sites SET last_seen_at = @now, first_seen_at = coalesce(first_seen_at, @now) WHERE id
    /// = @id AND (last_seen_at IS NULL OR last_seen_at &lt; @now - interval '1 minute')</c>. Called once
    /// per mint or renewal, but the row write itself happens at most once a minute per site - **say
    /// that cost in the code**, the item's own instruction, restated here because this is the one
    /// place a future caller decides whether to call it at all.
    /// </summary>
    Task RecordSightingAsync(SiteId siteId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// `23-06`'s other half of §3's amendment: the last origin a request for this site was refused
    /// for, and when. Under the classic failure this exists to catch - a `www.` vs. bare-domain
    /// mismatch, everything refused - a broken tenant's every visitor hits this exact branch just as
    /// often as a working one hits <see cref="RecordSightingAsync"/>, so this write is throttled the
    /// identical once-a-minute-per-site way, for the identical reason: one row write per site per
    /// minute under load, not one per rejected request.
    /// </summary>
    Task RecordRefusedOriginAsync(SiteId siteId, string origin, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// The four facts as they stand right now. Never <see langword="null"/> for a site that exists -
    /// a fresh row with every column still unset reads back as <see cref="SiteInstallationSignals.None"/>,
    /// not a missing record; the caller (<c>GetSiteInstallationHandler</c>) has already proven the site
    /// exists via <see cref="ISiteRepository.GetByIdAsync"/> before ever reaching this call.
    /// </summary>
    Task<SiteInstallationSignals> GetAsync(SiteId siteId, CancellationToken cancellationToken);
}

/// <summary>`23-06`: the four raw facts, carried as one value rather than four separate return values -
/// they are always read and reasoned about together (<c>SiteInstallationStateResolver.Resolve</c>
/// takes three of the four, the fourth joins the DTO unchanged).</summary>
public sealed record SiteInstallationSignals(
    DateTimeOffset? FirstSeenAt,
    DateTimeOffset? LastSeenAt,
    string? LastRefusedOrigin,
    DateTimeOffset? LastRefusedOriginAt)
{
    /// <summary>Every column unset - what a site that has never recorded a sighting or a refusal reads
    /// back as, the same "no row" hazard <see cref="ISiteInstallationSignalRepository.GetAsync"/>'s own
    /// remarks describe.</summary>
    public static readonly SiteInstallationSignals None = new(null, null, null, null);
}
