using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.SendMessage;

/// <summary><see cref="SiteId"/> scopes the <c>conversation:send</c> permission check
/// (adr/0016) - it comes from the operator's own token claims, not a lookup.</summary>
public sealed record SendOperatorMessage(
    ConversationId ConversationId, OperatorId AuthorId, SiteId SiteId, string Body, AttachmentId? AttachmentId = null,
    Guid? ClientMessageId = null, string? TraceParent = null);
