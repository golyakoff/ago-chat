namespace Ago.Chat.Worker;

/// <summary>Bound from <c>OperatorMessagePushConsumer:*</c> config keys, validated at startup - the
/// identical shape every sibling consumer's own options class already takes.</summary>
public sealed class OperatorMessagePushConsumerOptions
{
    public const string SectionName = "OperatorMessagePushConsumer";

    public int MaxAttempts { get; set; } = 5;

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);
}
