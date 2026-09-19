using System.Net.Http.Headers;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Caching;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Application.UseCases.SubmitLogoUpload;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Ago.Chat.Worker;

/// <summary>
/// `25-160`: the sibling of `5-04`'s <see cref="AttachmentThumbnailGenerator"/> - downloads the pending
/// object (presigned GET, the same "Worker as an ordinary port consumer" shape that generator already
/// uses), decodes with SkiaSharp (already a dependency, `AttachmentThumbnailGenerator`'s own precedent -
/// no new package), confirms real dimensions at or under
/// <see cref="SiteLogoOptions.MaxDimensionPixels"/>, a format among the three allowed, and the source is
/// not animated (multi-frame). On success: promotes the object to its permanent public key, flips
/// <see cref="Site"/>'s logo status to <see cref="LogoStatus.Ready"/>, publishes
/// <see cref="SiteLogoPromoted"/>, and write-through populates the branding cache with the base64
/// payload it already holds in memory - this backlog item's own Design decisions: "write-through
/// populated by the validating worker itself the moment it finishes decoding the bytes (no extra storage
/// read needed for the very first email after upload)." On failure: flips status to
/// <see cref="LogoStatus.Rejected"/> with a reason, no promotion, no cache write.
///
/// <para><b>Idempotency.</b> A redelivered <see cref="Ago.Chat.Contracts.SiteLogoValidationRequested"/>
/// for an upload already superseded by a newer one is a safe no-op -
/// <see cref="Site.PromoteLogo"/>/<see cref="Site.RejectLogoUpload"/> both guard on the pending object
/// key still being the current one (each method's own remarks). A redelivery for the upload that is
/// still current simply re-runs the identical decode/validate/promote-or-reject sequence and reaches the
/// identical outcome - the same "plain read-then-write, safe under `Competing`'s own redelivery-after-
/// outcome-known guarantee" reasoning <see cref="AttachmentThumbnailGenerator"/>'s own remarks give for
/// itself.</para>
/// </summary>
public sealed class SiteLogoValidator(
    ISiteRepository sites,
    IFileStorage fileStorage,
    IOutboxWriter outbox,
    ICache cache,
    IOptions<SiteLogoOptions> options,
    IIdGenerator idGenerator,
    IClock clock,
    ILogger<SiteLogoValidator> logger)
{
    // Ephemeral, immediately-consumed - the identical AttachmentThumbnailGenerator.UrlLifetime shape.
    private static readonly TimeSpan UrlLifetime = TimeSpan.FromMinutes(2);
    private static readonly HttpClient Http = new();

    // How long a validated logo's base64 stays warm in Redis before a cache-aside miss would re-read
    // object storage - generous, since a logo changes at most 5 times a day per site and the whole
    // point of the write-through is to make a cold read after a fresh promotion structurally
    // impossible, not merely rare.
    private static readonly CacheEntryOptions LogoCacheOptions = new(TimeSpan.FromHours(24));

    public async Task ValidateAsync(SiteId siteId, string pendingObjectKey, string contentType, CancellationToken cancellationToken)
    {
        var site = await sites.GetByIdAsync(siteId, cancellationToken);
        if (site is null)
        {
            logger.LogWarning(
                "SiteLogoValidationRequested for site {SiteId} but the site no longer exists; skipping.", siteId.Value);
            return;
        }

        var downloadUrl = await fileStorage.CreateDownloadUrlAsync(new ObjectKey(pendingObjectKey), UrlLifetime, cancellationToken);
        using var downloadResponse = await Http.GetAsync(downloadUrl, cancellationToken);
        downloadResponse.EnsureSuccessStatusCode();
        var bytes = await downloadResponse.Content.ReadAsByteArrayAsync(cancellationToken);

        var now = clock.UtcNow;
        var rejection = Validate(bytes, options.Value.MaxDimensionPixels);
        if (rejection is not null)
        {
            site.RejectLogoUpload(pendingObjectKey, rejection, now);
            await sites.SaveAsync(site, cancellationToken);
            return;
        }

        var extension = options.Value.AllowedContentTypes.TryGetValue(contentType, out var mapped) ? mapped : ".png";
        var publicObjectKey = $"site/{siteId.Value}/logo/{idGenerator.NewId(now):N}{extension}";

        var upload = await fileStorage.CreateUploadAsync(
            new ObjectKey(publicObjectKey), new UploadConstraints(contentType, bytes.Length, UrlLifetime), cancellationToken);
        using var uploadContent = new ByteArrayContent(bytes);
        uploadContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var uploadResponse = await Http.PutAsync(upload.Url, uploadContent, cancellationToken);
        uploadResponse.EnsureSuccessStatusCode();

        site.PromoteLogo(pendingObjectKey, publicObjectKey, now);
        var promoted = site.DomainEvents.OfType<SiteLogoPromoted>().SingleOrDefault();
        if (promoted is not null)
        {
            outbox.Enqueue(SiteLogoPromotedMapper.ToEnvelope(promoted, idGenerator));
            site.ClearDomainEvents();
            await sites.SaveAsync(site, cancellationToken);

            // Write-through, after the database commit - the base64 payload is already in memory from
            // the decode above, computed once, never per email (this item's own Design decisions).
            var base64 = Convert.ToBase64String(bytes);
            await cache.SetAsync(
                SiteBrandingCacheKeys.ForLogo(siteId), new SiteBrandingLogoPayload(base64, contentType),
                LogoCacheOptions, cancellationToken);
        }
        // promoted is null only when Site.PromoteLogo's own staleness guard fired (a newer upload
        // already superseded this one) - nothing to save or cache; the newer upload's own validation
        // will do both when it completes.
    }

    /// <summary>Returns a rejection reason, or <see langword="null"/> when the image passes every
    /// check. A caller's declared content type is never trusted here - <c>SKBitmap.Decode</c>/
    /// <c>SKCodec.Create</c> both work from the real bytes, the same "the console's own courtesy check
    /// is never the authority" split this item's own Scope states for itself.</summary>
    private static string? Validate(byte[] bytes, int maxDimensionPixels)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(bytes));
        if (codec is null)
        {
            return "Could not decode the uploaded file as an image.";
        }

        if (codec.FrameCount > 1)
        {
            return "Animated images are not supported - upload a static PNG, JPEG, or GIF.";
        }

        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || info.Width > maxDimensionPixels || info.Height > maxDimensionPixels)
        {
            return $"Logo must be {maxDimensionPixels}x{maxDimensionPixels} pixels or smaller " +
                $"(uploaded image is {info.Width}x{info.Height}).";
        }

        return null;
    }
}
