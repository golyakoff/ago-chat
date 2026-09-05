using Ago.Platform.Abstractions;

namespace Ago.Chat.Application.Caching;

/// <summary>
/// `24-02`: two key shapes for two different cacheability stories, not one - the reasoning is in
/// <c>GetDocumentVersionHandler</c>'s own remarks. <see cref="ForVersion"/> names an immutable row (a
/// published version's text never changes once written) and is safe to cache for a long time;
/// <see cref="ForCurrent"/> names a pointer that moves every time a new version publishes, and is
/// cached the same short-TTL way <see cref="SiteCacheKeys"/> already caches a site's own mutable
/// config.
/// </summary>
public static class DocumentCacheKeys
{
    public static CacheKey ForVersion(string documentKey, string version) => new($"document:{documentKey}:version:{version}");

    public static CacheKey ForCurrent(string documentKey) => new($"document:{documentKey}:current");
}
