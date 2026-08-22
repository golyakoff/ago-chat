using Ago.Chat.Domain;
using Ago.Platform.Abstractions;

namespace Ago.Chat.Application.Caching;

/// <summary>`5-03`: `file-storage.md`'s "cached per (attachment, viewer)" - keyed by both, since a
/// presigned GET URL is valid for anyone holding it, but two different viewers must never be handed
/// the same cached URL for an attachment neither of them has necessarily been re-checked against by
/// the time the other's copy is still warm.</summary>
public static class AttachmentCacheKeys
{
    public static CacheKey ForDownload(AttachmentId attachmentId, Guid viewerId) =>
        new($"attachment-download:{attachmentId.Value}:{viewerId}");
}
