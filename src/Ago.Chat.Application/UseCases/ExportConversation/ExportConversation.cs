using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.ExportConversation;

/// <summary>`24-11`: export exactly one conversation and nothing else - the narrowest granularity
/// erasure already has (<c>RequestConversationErasure</c>'s own sibling), applied to export.</summary>
public sealed record ExportConversation(ConversationId ConversationId, OperatorId RequestedBy, SiteId SiteId);
