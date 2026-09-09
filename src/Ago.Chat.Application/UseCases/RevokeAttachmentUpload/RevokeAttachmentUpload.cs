using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RevokeAttachmentUpload;

public sealed record RevokeAttachmentUpload(ConversationId ConversationId, OperatorId RequestedBy, SiteId SiteId);
