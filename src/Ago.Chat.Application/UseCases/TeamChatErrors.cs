using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases;

/// <summary>Error codes for the team-chat use cases - its own small vocabulary rather than reusing
/// <see cref="ConversationErrors"/>, the same "kept in one place so a client branching on `type`
/// sees the same code regardless of which use case raised it" reasoning that class states for itself,
/// applied to a feature that is not about a conversation at all.</summary>
public static class TeamChatErrors
{
    public static Error InvalidBody(string reason) => new("TeamChat.InvalidBody", reason);

    public static Error NotFound(string reason) => new("TeamChat.NotFound", reason);
}
