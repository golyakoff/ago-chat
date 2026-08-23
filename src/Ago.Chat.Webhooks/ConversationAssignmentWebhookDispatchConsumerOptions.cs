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

    /// <summary>
    /// `6-07`: how many deliveries <see cref="ConcurrentWebhookDispatchPump"/> processes at once for
    /// this subscription, sized independently of the broker's own <c>PrefetchCount</c> - the whole
    /// point of `6-07`'s fix. 32 is an unmeasured starting point (the same category as `4-05`'s own
    /// <c>MessagePipelineOptions.WorkerCount</c> default), chosen only to comfortably exceed
    /// `6-06`'s own 25-conversation burst scenario and the per-tenant bulkhead's
    /// MaxConcurrency(4)+MaxQueuedActions(16)=20 total capacity - Stage 7's job to actually tune.
    /// </summary>
    public int MaxConcurrency { get; set; } = 32;

    /// <summary>`6-07`: the local bounded queue's capacity, feeding <see cref="MaxConcurrency"/>
    /// workers - an unmeasured starting point, generous enough to hold a full burst without the
    /// broker's own <c>ReceivedAsync</c> callback blocking on backpressure mid-burst.</summary>
    public int ChannelCapacity { get; set; } = 128;

    /// <summary>`6-07`: bounds how long <see cref="ConversationAssignmentWebhookDispatchConsumer.StopAsync"/>
    /// waits for in-flight and already-queued deliveries to drain - the same shape and same default as
    /// `4-05`'s <c>MessagePipelineOptions.ShutdownDrainTimeout</c>.</summary>
    public TimeSpan ShutdownDrainTimeout { get; set; } = TimeSpan.FromSeconds(20);
}
