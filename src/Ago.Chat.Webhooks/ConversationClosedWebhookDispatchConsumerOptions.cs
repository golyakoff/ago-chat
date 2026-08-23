namespace Ago.Chat.Webhooks;

/// <summary>Bound from <c>ConversationClosedWebhookDispatchConsumer:*</c> config keys - the
/// broker-level poison-message retry policy, the same shape and same distinction from
/// <c>Resilience:Webhooks:Retry:*</c> as <see cref="ConversationAssignmentWebhookDispatchConsumerOptions"/>'s
/// own remarks explain.</summary>
public sealed class ConversationClosedWebhookDispatchConsumerOptions
{
    public const string SectionName = "ConversationClosedWebhookDispatchConsumer";

    public int MaxAttempts { get; set; } = 5;

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);
}
