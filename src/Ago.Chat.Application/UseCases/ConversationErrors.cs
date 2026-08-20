using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases;

/// <summary>Error codes shared by the conversation use cases - kept in one place so a client
/// branching on <c>type</c> (api-design.md) sees the same code regardless of which use case raised
/// it.</summary>
internal static class ConversationErrors
{
    public static Error NotFound(Guid conversationId) =>
        new("Conversation.NotFound", $"Conversation {conversationId} was not found.");

    public static Error Forbidden(string reason) =>
        new("Conversation.Forbidden", reason);

    public static Error InvalidState(string reason) =>
        new("Conversation.InvalidState", reason);

    public static Error InvalidBody(string reason) =>
        new("Message.InvalidBody", reason);
}
