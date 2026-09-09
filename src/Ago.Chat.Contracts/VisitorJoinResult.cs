namespace Ago.Chat.Contracts;

/// <summary>What <c>/hubs/visitor</c>'s <c>Join</c> method returns - the conversation to render and
/// whether it is brand new (worth a greeting) or resumed (worth "welcome back").
///
/// `23-78`: <see cref="HasAttachmentUploadGrant"/> joins additively - whether this conversation
/// currently carries a visitor-side attachment-upload grant (<c>Domain.Conversation.HasAttachmentUploadGrant</c>),
/// read once per join. This is the widget's one and only channel for the fact -
/// <c>CreateAttachmentHandler.HandleAsVisitorAsync</c> is the real control (it refuses the presigned
/// slot server-side regardless of what this field says); the widget uses it only to decide whether to
/// show its upload icon at all (the backlog item's own "hiding the icon is not the control... the
/// widget's missing icon is a consequence"). No live push for a grant that changes mid-session, the
/// identical gap `Domain.Conversation.IsBlocked`'s own consumers already accept for the same reason
/// (nothing in this codebase pushes a block/unblock to a connected visitor's own client either) - a
/// visitor sees the icon appear or disappear on the next reconnect/page load, not instantly the moment
/// an operator toggles it.</summary>
public sealed record VisitorJoinResult(
    Guid ConversationId, bool IsNew, IReadOnlyList<MessageDto> History, bool HasAttachmentUploadGrant = false);
