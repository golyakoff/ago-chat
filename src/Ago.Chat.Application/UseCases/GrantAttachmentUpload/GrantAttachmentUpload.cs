using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GrantAttachmentUpload;

public sealed record GrantAttachmentUpload(ConversationId ConversationId, OperatorId RequestedBy, SiteId SiteId);
