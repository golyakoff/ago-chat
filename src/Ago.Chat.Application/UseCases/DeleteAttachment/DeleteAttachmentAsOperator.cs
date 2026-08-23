using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.DeleteAttachment;

/// <summary>`5-08`: operator-only - a visitor never held `attachment:delete` and `authorization.md`
/// never proposed one for them (`Visitor` "stays outside the role system" entirely, adr/0016), so
/// unlike every other attachment use case this has no `AsVisitor` twin.</summary>
public sealed record DeleteAttachmentAsOperator(AttachmentId AttachmentId, OperatorId RequestedBy, SiteId SiteId);
