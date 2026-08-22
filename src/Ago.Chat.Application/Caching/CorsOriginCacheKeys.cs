using Ago.Platform.Abstractions;

namespace Ago.Chat.Application.Caching;

/// <summary>`5-01`: the CORS-layer counterpart to <see cref="SiteCacheKeys"/> - keyed by the caller's
/// `Origin`, not a public key, because that is the only piece of data a CORS preflight ever carries.</summary>
public static class CorsOriginCacheKeys
{
    public static CacheKey ForOrigin(string origin) => new($"cors-origin:{origin}");
}
