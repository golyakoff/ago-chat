using System.Text.Json;
using Ago.Chat.Application.UseCases.DispatchWebhooksForEvent;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Webhooks;

/// <summary>
/// `6-05`: the first of this item's two new `Competing` consumers of `ConversationAssignedToOperator`
/// - `Ago.Chat.Worker.ConversationAssignmentFanoutConsumer` (`4-02`) already subscribes to the exact
/// same topic in the exact same mode, so this is a live instance of the "two different consumer types
/// sharing one topic" shape `5-11`'s fix exists for: <c>ConsumerName</c> below is deliberately its own
/// stable string, distinct from `ConversationAssignmentFanoutConsumer.ConsumerName` and from every
/// other consumer of any topic in this repository - reusing either name (or leaving it out, if the
/// port still allowed that) would silently share Worker's own queue and split messages between the two
/// consumer types again, the exact bug `5-11` fixed once already
/// (`WebhookDispatchSharedQueueRegressionTests` in `Ago.Chat.Integration.Tests` proves this
/// consumer specifically, not just the platform's own general fix, against a real broker).
///
/// Runs in `Ago.Chat.Webhooks`, not `Ago.Chat.Worker` - `adr/0013`'s entire reason for a third
/// deployable: this consumer's own work (an outbound HTTP call that may hang for 30s) must never share
/// a process with `ConversationAssignmentFanoutConsumer`'s realtime fan-out or the outbox dispatcher.
///
/// `6-07`: `RabbitMqEventConsumer.SubscribeAsync` awaits its handler delegate inline, so at most one
/// delivery was ever processed at a time for this subscription regardless of `PrefetchCount`
/// (`6-06`'s own load-proof found this live - the per-tenant bulkhead's `MaxConcurrency=4` was
/// structurally unreachable). The delegate registered below is now the fast one
/// <see cref="ConcurrentWebhookDispatchPump"/>'s own remarks describe - it only enqueues and returns;
/// <see cref="ProcessOneAsync"/> (this class's own former <c>HandleAsync</c>) is the real handler,
/// now invoked by the pump's worker loops instead of directly by the broker client.
/// </summary>
public sealed class ConversationAssignmentWebhookDispatchConsumer(
    IEventConsumer consumer,
    IServiceScopeFactory scopeFactory,
    IOptions<ConversationAssignmentWebhookDispatchConsumerOptions> options,
    ILogger<ConversationAssignmentWebhookDispatchConsumer> logger) : BackgroundService
{
    private const string ConsumerName = "conversation-assignment-webhook-dispatch";

    private ConcurrentWebhookDispatchPump? _pump;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryPolicy = new RetryPolicy(
            options.Value.MaxAttempts, options.Value.InitialBackoff, $"{ConsumerName}.dlq");

        var pump = new ConcurrentWebhookDispatchPump(
            options.Value.MaxConcurrency, options.Value.ChannelCapacity, ProcessOneAsync, logger, ConsumerName, stoppingToken);
        _pump = pump;

        return consumer.SubscribeAsync(
            nameof(ConversationAssignedToOperator), SubscriptionMode.Competing, ConsumerName, retryPolicy,
            (envelope, context, ct) => pump.EnqueueAsync(envelope, context, ct).AsTask(), stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(options.Value.ShutdownDrainTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        await base.StopAsync(linked.Token);

        if (_pump is not null)
        {
            await _pump.DrainAsync().WaitAsync(linked.Token);
        }
    }

    private async Task ProcessOneAsync(EventEnvelope envelope, IMessageContext context, CancellationToken cancellationToken)
    {
        try
        {
            var contract = JsonSerializer.Deserialize<ConversationAssignedToOperator>(envelope.Payload)
                ?? throw new InvalidOperationException(
                    $"Could not deserialize {nameof(ConversationAssignedToOperator)} payload for outbox message {envelope.MessageId}.");

            await using var scope = scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<DispatchWebhooksForEventHandler>();

            var command = new DispatchWebhooksForConversationEvent(
                envelope.MessageId, new SiteId(contract.SiteId), contract.ConversationId,
                WebhookEventTypes.ConversationAssigned, contract.OccurredAt);

            var result = await handler.HandleAsync(command, cancellationToken);
            if (result.IsFailure)
            {
                throw new InvalidOperationException($"{result.Error!.Value.Code}: {result.Error!.Value.Message}");
            }

            await context.AckAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to dispatch webhooks for {MessageId}.", envelope.MessageId);
            throw;
        }
    }
}
