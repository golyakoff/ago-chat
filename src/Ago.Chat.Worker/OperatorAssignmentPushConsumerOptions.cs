namespace Ago.Chat.Worker;

/// <summary>Bound from <c>OperatorAssignmentPushConsumer:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule) - the identical shape every sibling consumer's
/// own options class already takes (<see cref="ConversationAssignmentFanoutConsumerOptions"/>).</summary>
public sealed class OperatorAssignmentPushConsumerOptions
{
    public const string SectionName = "OperatorAssignmentPushConsumer";

    public int MaxAttempts { get; set; } = 5;

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);
}
