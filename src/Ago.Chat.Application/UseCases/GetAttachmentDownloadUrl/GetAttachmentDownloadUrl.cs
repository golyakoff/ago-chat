using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetAttachmentDownloadUrl;

public sealed record GetAttachmentDownloadUrlAsVisitor(AttachmentId AttachmentId, VisitorId RequestedBy);

public sealed record GetAttachmentDownloadUrlAsOperator(AttachmentId AttachmentId, OperatorId RequestedBy, SiteId SiteId);

/// <summary>`5-10`: <see cref="ThumbnailUrl"/> is null whenever `5-04`'s async job has not (yet, or
/// ever - a non-image attachment never gets one) produced one - a widget/console client falls back
/// to <see cref="ContentType"/> to decide how to render the attachment itself in that case, never a
/// guess from the URL's own extension.</summary>
public sealed record AttachmentDownload(Uri Url, string ContentType, Uri? ThumbnailUrl, DateTimeOffset ExpiresAt);
