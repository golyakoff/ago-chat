namespace Ago.Chat.Contracts;

/// <summary>
/// `23-48`: the CORS-layer counterpart to <see cref="SiteSettingsChanged"/> - carries what
/// <c>Ago.Chat.Worker</c>'s <c>SiteAllowedOriginsCacheInvalidationConsumer</c> needs to evict
/// <c>Ago.Chat.Application.Caching.CorsOriginCacheKeys.ForOrigin</c> for every origin a write touched.
///
/// <para><b>Why this is a second contract rather than a field added to <see cref="SiteSettingsChanged"/>.</b>
/// <see cref="SiteSettingsChanged"/> is produced by five other <c>Site</c> write paths
/// (<c>SiteWidgetConfigUpdated</c>, <c>SiteOfflineAutoReplyUpdated</c>, <c>SiteLocaleUpdated</c>,
/// <c>SiteAssignmentPenaltyUpdated</c>, <c>SiteSubscriptionActivated</c>, <c>SiteContactVisibilityUpdated</c>)
/// none of which ever touch <see cref="Domain.Site.AllowedOrigins"/> - giving that contract a
/// <c>PreviousOrigins</c>/<c>AllowedOrigins</c> pair every other producer would have to leave null
/// would make the shared contract lie about what most of its own producers changed. A second,
/// narrowly-scoped contract with exactly the two producers that need it (this event's mapper, and
/// nothing else) keeps <see cref="SiteSettingsChanged"/> honest for its other five.</para>
///
/// <para>Carries both <see cref="PreviousOrigins"/> and <see cref="AllowedOrigins"/>, not a delta -
/// see <see cref="Domain.SiteAllowedOriginsUpdated"/>'s own remarks for why the consumer needs both
/// the removed and the added origins to evict every stale cache entry a write could have left
/// behind.</para>
/// </summary>
public sealed record SiteAllowedOriginsChanged(
    Guid MessageId,
    DateTimeOffset OccurredAt,
    Guid SiteId,
    Guid CorrelationId,
    string PublicKey,
    IReadOnlyList<string> PreviousOrigins,
    IReadOnlyList<string> AllowedOrigins);
