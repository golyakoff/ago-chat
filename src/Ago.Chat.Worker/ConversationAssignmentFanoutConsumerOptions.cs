namespace Ago.Chat.Worker;

/// <summary>Bound from <c>ConversationAssignmentFanoutConsumer:*</c> config keys, validated at
/// startup (naming-and-structure.md's options-validation rule).</summary>
public sealed class ConversationAssignmentFanoutConsumerOptions
{
    public const string SectionName = "ConversationAssignmentFanoutConsumer";

    public int MaxAttempts { get; set; } = 5;

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);
}
