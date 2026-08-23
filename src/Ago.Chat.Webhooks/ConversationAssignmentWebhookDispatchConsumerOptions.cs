namespace Ago.Chat.Webhooks;

/// <summary>Bound from <c>ConversationAssignmentWebhookDispatchConsumer:*</c> config keys, validated
/// at startup (naming-and-structure.md's options-validation rule) - the broker-level poison-message
/// retry policy (messaging.md: "N attempts with exponential backoff, then dead-letter with the full
/// envelope"), not to be confused with <c>Resilience:Webhooks:Retry:*</c>
/// (<see cref="WebhookHttpOptions"/>'s sibling, <c>ResiliencePipelineOptions.Retry</c>) - that one
/// governs retrying one endpoint's own HTTP delivery inside a single successful handler invocation;
/// this one governs the broker requeuing the whole message if the handler itself throws
/// unexpectedly.</summary>
public sealed class ConversationAssignmentWebhookDispatchConsumerOptions
{
    public const string SectionName = "ConversationAssignmentWebhookDispatchConsumer";

    public int MaxAttempts { get; set; } = 5;

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);
}
