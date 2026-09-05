using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Caching;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetDocumentVersion;

/// <summary>
/// `24-02`'s own published surface, read side - the endpoint an unauthenticated caller (`24-02`'s own
/// Scope: no account exists yet for someone who has not accepted anything) actually reaches. Cache-aside
/// via <see cref="ICache.GetOrCreateAsync{T}"/>, the same mechanism `GetSiteConfigByPublicKeyHandler`
/// already uses for the other hot, unauthenticated read this codebase has - "this handler only decides
/// what to cache and for how long, never how" applies here verbatim.
///
/// <para><b>Two different TTLs for two different cacheability stories, not one.</b> A request naming a
/// specific <see cref="GetDocumentVersion.Version"/> is asking about an immutable row - once published,
/// a version's text never changes (`Domain.PublishedDocumentVersion`'s own remarks) - so it is cached
/// for a long time (<see cref="VersionTtl"/>). A request asking for the current version is asking about
/// a pointer that moves the moment a new version publishes, so it gets the same short TTL
/// <see cref="GetSiteConfigByPublicKeyHandler"/> already uses for a site's own mutable config
/// (<see cref="CurrentTtl"/>), backstopped by <c>PublishDocumentVersionHandler</c>'s own active eviction
/// of exactly that key on every successful publish - belt and braces, the same pairing `caching.md`
/// already recommends for a value that changes rarely but must never be seen stale for long.</para>
/// </summary>
public sealed class GetDocumentVersionHandler(IDocumentRepository documents, ICache cache)
{
    private static readonly CacheEntryOptions VersionTtl = new(TimeSpan.FromHours(24));
    private static readonly CacheEntryOptions CurrentTtl = new(TimeSpan.FromMinutes(5));

    // caching.md: negative caching gets its own, shorter TTL than a real hit - the same reasoning
    // GetSiteConfigByPublicKeyHandler's own remarks give, so a document published moments ago (or a
    // typo'd version corrected a moment later) is not stuck reading "not found" for the full window.
    private static readonly CacheEntryOptions NegativeTtl = new(TimeSpan.FromSeconds(30));

    public async Task<Result<PublishedDocumentVersionDto>> HandleAsync(GetDocumentVersion query, CancellationToken cancellationToken)
    {
        var key = query.Version is { Length: > 0 } version
            ? DocumentCacheKeys.ForVersion(query.DocumentKey, version)
            : DocumentCacheKeys.ForCurrent(query.DocumentKey);
        var ttl = query.Version is { Length: > 0 } ? VersionTtl : CurrentTtl;

        var result = await cache.GetOrCreateAsync(key, ct => LoadAsync(query, key, ct), ttl, cancellationToken);
        return result.Found ? result.Dto! : PublishedDocumentErrors.NotFound(query.DocumentKey);
    }

    private async Task<DocumentLookupResult> LoadAsync(GetDocumentVersion query, CacheKey key, CancellationToken cancellationToken)
    {
        var version = query.Version is { Length: > 0 } specific
            ? await documents.FindVersionAsync(query.DocumentKey, specific, cancellationToken)
            : await documents.FindCurrentAsync(query.DocumentKey, cancellationToken);

        if (version is null)
        {
            // Written here, directly, with NegativeTtl - not left to GetOrCreateAsync's own
            // post-factory populate step, which would apply the positive TTL instead
            // (GetSiteConfigByPublicKeyHandler's own remarks on this exact pattern).
            await cache.SetAsync(key, DocumentLookupResult.NotFound, NegativeTtl, cancellationToken);
            return DocumentLookupResult.NotFound;
        }

        return DocumentLookupResult.Of(ToDto(version));
    }

    private static PublishedDocumentVersionDto ToDto(PublishedDocumentVersion version) =>
        new(version.DocumentKey, version.Version, version.Sequence, version.Title, version.Body, version.PublishedAt);
}

/// <summary>What actually gets cached - <see cref="PublishedDocumentVersionDto"/>, never
/// <see cref="Ago.Chat.Domain.PublishedDocumentVersion"/> itself (the same Domain/Application boundary
/// <c>SiteLookupResult</c> already draws for <c>Site</c>/<c>SiteConfigDto</c>), wrapped so a cache miss
/// and a confirmed not-found stay distinguishable - the identical role <c>SiteLookupResult</c> plays
/// for <c>GetSiteConfigByPublicKeyHandler</c>.</summary>
internal sealed record DocumentLookupResult(bool Found, PublishedDocumentVersionDto? Dto)
{
    public static readonly DocumentLookupResult NotFound = new(false, null);

    public static DocumentLookupResult Of(PublishedDocumentVersionDto dto) => new(true, dto);
}
