namespace Ago.Chat.Module.Branding;

/// <summary>
/// `25-160`: a small, read-only shadow of `Ago.Platform.Storage.S3`'s own <c>Storage:S3</c> section -
/// bound a second time here, deliberately, rather than widening <c>Ago.Platform.Abstractions.IFileStorage</c>
/// with a "give me the public URL" method (<see cref="Application.Abstractions.ISiteLogoPublicUrlBuilder"/>'s
/// own remarks on why that platform change is out of this item's own reach). <see cref="ServiceUrl"/> is
/// already the public, internet-reachable hostname on the live deployment (`file-storage.md`'s own `25-103`
/// note: "adds a real public hostname... and points `Storage__S3__ServiceUrl` at it"), so this class reads
/// exactly the same two configuration keys the platform's own `AddS3FileStorage` already binds, for the
/// one additional purpose that port has no method for.
/// </summary>
public sealed class SiteBrandingStorageOptions
{
    public const string SectionName = "Storage:S3";

    public string ServiceUrl { get; set; } = string.Empty;

    public string Bucket { get; set; } = string.Empty;
}
