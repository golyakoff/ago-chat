using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.ExportVisitor;

/// <summary>`24-11`: export every conversation belonging to the same visitor as
/// <see cref="ConversationId"/> - anchored via a conversation the operator already holds, the same
/// "reach the visitor through the conversation an operator is looking at" shape
/// <c>GetVisitorHistory</c>/<c>ListChannelIdentitiesForVisitor</c> already establish, applied here to
/// resolve which visitor to export rather than which visitor's other conversations to list.</summary>
public sealed record ExportVisitor(ConversationId ConversationId, OperatorId RequestedBy, SiteId SiteId);
