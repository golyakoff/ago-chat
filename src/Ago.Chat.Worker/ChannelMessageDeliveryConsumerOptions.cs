namespace Ago.Chat.Worker;

/// <summary>Bound from <c>ChannelMessageDeliveryConsumer:*</c> config keys, validated at startup -
/// <see cref="OfflineAutoReplyConsumerOptions"/>'s own shape for the other consumer of this
/// topic.</summary>
public sealed class ChannelMessageDeliveryConsumerOptions
{
    public const string SectionName = "ChannelMessageDeliveryConsumer";

    public int MaxAttempts { get; set; } = 5;

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);
}
