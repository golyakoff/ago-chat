namespace Ago.Chat.Worker;

/// <summary>Bound from <c>TeamMessageRemovedFanoutConsumer:*</c> config keys, the removal sibling of
/// <see cref="TeamChatFanoutConsumerOptions"/> (naming-and-structure.md's options-validation
/// rule) - its own section rather than a shared one, the same one-consumer-one-section shape every
/// other consumer's options class in this project already takes.</summary>
public sealed class TeamMessageRemovedFanoutConsumerOptions
{
    public const string SectionName = "TeamMessageRemovedFanoutConsumer";

    public int MaxAttempts { get; set; } = 5;

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);
}
