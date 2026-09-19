using Ago.Chat.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Module.Branding;

/// <summary>`25-160`: a plain string join, no signing, no network call - <see cref="ISiteLogoPublicUrlBuilder"/>'s
/// own remarks on why a promoted logo's URL is computed, not presigned.</summary>
public sealed class ConfiguredSiteLogoPublicUrlBuilder(IOptions<SiteBrandingStorageOptions> options)
    : ISiteLogoPublicUrlBuilder
{
    public string Build(string objectKey)
    {
        var value = options.Value;
        return $"{value.ServiceUrl.TrimEnd('/')}/{value.Bucket}/{objectKey}";
    }
}
