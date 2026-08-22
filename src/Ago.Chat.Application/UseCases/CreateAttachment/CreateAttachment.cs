using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.CreateAttachment;

public sealed record CreateAttachmentAsVisitor(
    ConversationId ConversationId, VisitorId RequestedBy, string ContentType, long DeclaredSizeBytes);

/// <summary><see cref="SiteId"/> comes from the operator's own token claims, not a lookup - the same
/// reason <c>SendOperatorMessage</c> carries it (RBAC's `conversation:send` check needs it before any
/// conversation is loaded).</summary>
public sealed record CreateAttachmentAsOperator(
    ConversationId ConversationId, OperatorId RequestedBy, SiteId SiteId, string ContentType, long DeclaredSizeBytes);

public sealed record PresignedAttachmentUpload(Guid AttachmentId, Uri UploadUrl, DateTimeOffset ExpiresAt);
