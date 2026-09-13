using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.BlockVisitor;

/// <summary>`23-77`: addressed by the conversation the operator is looking at, the same shape
/// `24-10`'s own <c>BlockConversation</c> uses - <see cref="BlockVisitorHandler"/>'s own remarks
/// explain why this writes a visitor-scoped restriction instead of (not through) that mechanism.
/// </summary>
public sealed record BlockVisitor(ConversationId ConversationId, OperatorId OperatorId, SiteId SiteId);
