namespace Ago.Chat.Worker;

/// <summary>Bound from <c>PhoneVerificationDeliveryConsumer:*</c> config keys, validated at startup -
/// <see cref="ChannelMessageDeliveryConsumerOptions"/>'s own shape for the other outbound-provider-call
/// consumer in this host.</summary>
public sealed class PhoneVerificationDeliveryConsumerOptions
{
    public const string SectionName = "PhoneVerificationDeliveryConsumer";

    public int MaxAttempts { get; set; } = 5;

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);
}
