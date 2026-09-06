namespace Ago.Chat.Worker;

/// <summary>Bound from <c>TeamChatFanoutConsumer:*</c> config keys, the team-chat sibling of
/// <see cref="ConnectionFanoutConsumerOptions"/> (naming-and-structure.md's options-validation
/// rule).</summary>
public sealed class TeamChatFanoutConsumerOptions
{
    public const string SectionName = "TeamChatFanoutConsumer";

    public int MaxAttempts { get; set; } = 5;

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);
}
