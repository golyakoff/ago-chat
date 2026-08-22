using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.ConfirmAttachment;

public sealed record ConfirmAttachmentAsVisitor(AttachmentId AttachmentId, VisitorId RequestedBy);

public sealed record ConfirmAttachmentAsOperator(AttachmentId AttachmentId, OperatorId RequestedBy, SiteId SiteId);
