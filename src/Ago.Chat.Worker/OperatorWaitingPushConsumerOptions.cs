namespace Ago.Chat.Worker;

/// <summary>Bound from <c>OperatorWaitingPushConsumer:*</c> config keys, validated at startup - the
/// identical shape every sibling consumer's own options class already takes.</summary>
public sealed class OperatorWaitingPushConsumerOptions
{
    public const string SectionName = "OperatorWaitingPushConsumer";

    public int MaxAttempts { get; set; } = 5;

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);
}
