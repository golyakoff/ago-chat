namespace Ago.Chat.Contracts;

/// <summary>What <c>/hubs/visitor</c>'s <c>Join</c> method returns - the conversation to render and
/// whether it is brand new (worth a greeting) or resumed (worth "welcome back").</summary>
public sealed record VisitorJoinResult(Guid ConversationId, bool IsNew, IReadOnlyList<MessageDto> History);
