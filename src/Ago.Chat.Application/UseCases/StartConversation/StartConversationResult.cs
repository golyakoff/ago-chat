using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.StartConversation;

/// <summary>`23-78`: <see cref="HasAttachmentUploadGrant"/> joins as an additive field - the widget's
/// own join result (<c>VisitorHub.JoinCoreAsync</c>, `Ago.Chat.Contracts.VisitorJoinResult`) needs to
/// know whether to show its upload control for *this* conversation, and
/// <see cref="StartConversationHandler"/> already has the loaded-or-just-created
/// <see cref="Conversation"/> in hand when it builds this result - reading
/// <see cref="Conversation.HasAttachmentUploadGrant"/> off it here costs nothing a second query would
/// have.</summary>
public sealed record StartConversationResult(ConversationId ConversationId, bool IsNew, bool HasAttachmentUploadGrant);
