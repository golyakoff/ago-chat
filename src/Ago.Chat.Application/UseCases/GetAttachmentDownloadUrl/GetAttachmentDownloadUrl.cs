using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetAttachmentDownloadUrl;

public sealed record GetAttachmentDownloadUrlAsVisitor(AttachmentId AttachmentId, VisitorId RequestedBy);

public sealed record GetAttachmentDownloadUrlAsOperator(AttachmentId AttachmentId, OperatorId RequestedBy, SiteId SiteId);

public sealed record AttachmentDownload(Uri Url, DateTimeOffset ExpiresAt);
