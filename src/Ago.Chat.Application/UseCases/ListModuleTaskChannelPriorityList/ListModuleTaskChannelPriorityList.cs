using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.ListModuleTaskChannelPriorityList;

/// <summary>`20-11`: the read behind whatever console surface eventually renders this list -
/// <see cref="ListChannelIdentitiesForVisitor.ListChannelIdentitiesForVisitor"/>'s own shape, reused
/// unchanged (`ConversationId` is both "which booking" via `Conversation.ActiveModuleTask` and the
/// per-conversation permission anchor).</summary>
public sealed record ListModuleTaskChannelPriorityList(ConversationId ConversationId, OperatorId RequestedBy, SiteId SiteId);
