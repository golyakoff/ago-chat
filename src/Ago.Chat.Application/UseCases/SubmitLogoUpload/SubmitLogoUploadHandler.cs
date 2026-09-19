using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.SubmitLogoUpload;

/// <summary>
/// `25-160`: this backlog item's own Scope, point 4, followed in the exact order it states: "a
/// synchronous, cheap check only - raw byte-size ceiling... before anything is decoded - then
/// rate-limited (5/site/day), then writes the bytes to a `pending` object key via `IFileStorage` and
/// stages an outbox event in the same transaction that records the `pending` status."
///
/// <para><b>Pushes the bytes itself, synchronously, rather than handing the console a presigned PUT
/// URL the way <see cref="CreateAttachment.CreateAttachmentHandler"/> does for a chat attachment.</b>
/// The console already has the file's bytes in memory (it read them client-side to run its own courtesy
/// dimension check before ever calling this endpoint), so a second round trip through a presigned URL
/// would buy nothing - this method presigns via <c>IFileStorage</c> and then pushes through
/// <see cref="IPresignedUrlUploader"/>, never a bare <c>HttpClient</c> of its own (`CLAUDE.md` rule 2 -
/// that port's own remarks explain why the identical "presign, then PUT" shape
/// <c>Ago.Chat.Worker.AttachmentThumbnailGenerator</c> proves in production cannot be inlined here the
/// way it is there: that class lives in a host project, this handler lives in Application).</para>
///
/// <para>References <c>Ago.Platform.Abstractions.IFileStorage</c> directly, the identical
/// "generic technical port, safe for Application to reference inwards" reasoning
/// <see cref="CreateAttachment.CreateAttachmentHandler"/>'s own remarks state.</para>
/// </summary>
public sealed class SubmitLogoUploadHandler(
    ISiteRepository sites,
    IFileStorage fileStorage,
    IPresignedUrlUploader uploader,
    IRateLimiter rateLimiter,
    IPermissionChecker permissions,
    IOutboxWriter outbox,
    SiteLogoOptions options,
    LogoUploadRateLimitOptions rateLimitOptions,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<LogoUploadAccepted>> HandleAsync(SubmitLogoUpload command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to configure this site's branding.");
        }

        // The one, cheap, decode-nothing check this handler ever runs - SiteLogoOptions' own remarks.
        if (!options.AllowedContentTypes.TryGetValue(command.ContentType, out var extension))
        {
            return ConversationErrors.SiteLogoInvalidContentType(command.ContentType);
        }

        if (command.Bytes.Length <= 0 || command.Bytes.Length > options.MaxSizeBytes)
        {
            return ConversationErrors.SiteLogoTooLarge(command.Bytes.Length, options.MaxSizeBytes);
        }

        var limit = await rateLimiter.CheckAsync(
            new RateLimitKey($"logo-upload:site:{command.SiteId.Value}"),
            new RateLimitRule(rateLimitOptions.PerSiteCapacity, rateLimitOptions.PerSiteRefillPerSecond),
            cancellationToken);
        if (!limit.Allowed)
        {
            return ConversationErrors.SiteLogoUploadRateLimited(limit.RetryAfter);
        }

        var site = await sites.GetByIdAsync(command.SiteId, cancellationToken);
        if (site is null)
        {
            return ConversationErrors.SiteNotFound(command.SiteId.Value);
        }

        var now = clock.UtcNow;
        // A "pending" prefix, distinct from the "logo" prefix PromoteLogo's own public key uses
        // (`Ago.Chat.Worker.SiteLogoValidator`'s own remarks) - a deployment's own MinIO bucket policy
        // can grant anonymous GET on the public prefix alone, never on this one.
        var objectKey = $"site/{command.SiteId.Value}/logo/pending/{idGenerator.NewId(now):N}{extension}";

        var presigned = await fileStorage.CreateUploadAsync(
            new ObjectKey(objectKey),
            new UploadConstraints(command.ContentType, command.Bytes.Length, options.InternalUploadLifetime),
            cancellationToken);
        await uploader.PutAsync(presigned.Url, command.ContentType, command.Bytes, cancellationToken);

        site.SubmitLogoUpload(objectKey, command.ContentType, now);
        var submitted = site.DomainEvents.OfType<SiteLogoUploadSubmitted>().Single();
        outbox.Enqueue(SiteLogoUploadSubmittedMapper.ToEnvelope(submitted, idGenerator));
        site.ClearDomainEvents();

        await sites.SaveAsync(site, cancellationToken);

        return new LogoUploadAccepted(LogoStatus.Pending);
    }
}
