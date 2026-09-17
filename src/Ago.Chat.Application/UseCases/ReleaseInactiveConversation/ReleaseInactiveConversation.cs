using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.ReleaseInactiveConversation;

/// <summary>
/// `25-118`: one candidate `AutoCloseInactiveConversationsJob` (`Ago.Chat.Worker`) found past its
/// `WidgetInactivityWindow` - the widget-only "release" pass this item adds alongside the job's
/// existing "close" pass (<see cref="Application.UseCases.AutoCloseConversation.AutoCloseConversation"/>).
/// Deliberately just the id, for the identical reason that sibling command states it: nobody is acting
/// on anybody's behalf, so there is no <c>OperatorId</c> to carry.
/// </summary>
public sealed record ReleaseInactiveConversation(ConversationId ConversationId);
