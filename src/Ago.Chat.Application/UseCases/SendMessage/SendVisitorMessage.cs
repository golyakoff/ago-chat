using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.SendMessage;

public sealed record SendVisitorMessage(
    ConversationId ConversationId, VisitorId AuthorId, string Body, AttachmentId? AttachmentId = null,
    Guid? ClientMessageId = null, string? TraceParent = null);
