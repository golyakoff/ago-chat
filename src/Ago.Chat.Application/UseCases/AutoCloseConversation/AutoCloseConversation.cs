using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.AutoCloseConversation;

/// <summary>
/// `18-06`: one candidate `AutoCloseInactiveConversationsJob` (`Ago.Chat.Worker`) found past its
/// per-channel-kind inactivity window. Deliberately just the id - unlike
/// <see cref="Application.UseCases.CloseConversation.CloseConversation"/>, there is no
/// <c>OperatorId</c> to carry, because nobody is acting on anybody's behalf (this type's own
/// handler remarks explain why that is a second handler rather than a nullable field on the first).
/// </summary>
public sealed record AutoCloseConversation(ConversationId ConversationId);
