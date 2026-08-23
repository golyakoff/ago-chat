using System.Text.Json;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.DispatchWebhooksForEvent;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Webhooks;

/// <summary>
/// `6-05`: this dispatcher's second consumer, `ConversationEnded` (`6-02`'s wire name for
/// `Domain.ConversationClosed`, `messaging.md`). Unlike `ConversationAssignedToOperator`, no other
/// consumer subscribes to this topic yet (`messaging.md`'s own table lists only "fan-out, cache
/// invalidation, metrics, 6-05's webhook dispatch" for it, all still unimplemented before this item),
/// so this is not itself a live instance of `5-11`'s shared-queue bug the way
/// <see cref="ConversationAssignmentWebhookDispatchConsumer"/> is - `ConsumerName` below is still its
/// own distinct, stable string regardless, both because a future second consumer of this topic must
/// not be able to collide with it by accident, and because the platform port requires one either way
/// (`IEventConsumer.SubscribeAsync`'s own doc comment: "every subscriber still has a name worth
/// logging").
///
/// The contract itself carries no `SiteId` (`ConversationEnded`'s own remarks: "no visitor/operator
/// identity... a webhook receiver only needs to know which conversation closed") - unlike
/// `ConversationAssignmentWebhookDispatchConsumer`, this handler loads the `Conversation` aggregate to
/// resolve it, the same "the event does not carry everything this consumer needs, so it loads what is
/// missing" shape `ResolveMessageDeliveryTargetsHandler` already uses for its own conversation load. A
/// missing conversation here is a genuine anomaly (`ConversationEnded` can only ever have been
/// published for one that existed and just closed, `adr/0005`'s "outbox commits in the same
/// transaction as the state change") - thrown, not swallowed, so it dead-letters through the normal
/// poison-message path instead of silently dropping a tenant's webhook.
///
/// `6-07`: see <see cref="ConversationAssignmentWebhookDispatchConsumer"/>'s own remarks - the same
/// <see cref="ConcurrentWebhookDispatchPump"/> fix, applied to this consumer's own separate
/// subscription/channel. <see cref="ProcessOneAsync"/> is this class's own former <c>HandleAsync</c>.
/// </summary>
public sealed class ConversationClosedWebhookDispatchConsumer(
    IEventConsumer consumer,
    IServiceScopeFactory scopeFactory,
    IOptions<ConversationClosedWebhookDispatchConsumerOptions> options,
    ILogger<ConversationClosedWebhookDispatchConsumer> logger) : BackgroundService
{
    private const string ConsumerName = "conversation-closed-webhook-dispatch";

    private ConcurrentWebhookDispatchPump? _pump;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryPolicy = new RetryPolicy(
            options.Value.MaxAttempts, options.Value.InitialBackoff, $"{ConsumerName}.dlq");

        var pump = new ConcurrentWebhookDispatchPump(
            options.Value.MaxConcurrency, options.Value.ChannelCapacity, ProcessOneAsync, logger, ConsumerName, stoppingToken);
        _pump = pump;

        return consumer.SubscribeAsync(
            nameof(ConversationEnded), SubscriptionMode.Competing, ConsumerName, retryPolicy,
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
            var contract = JsonSerializer.Deserialize<ConversationEnded>(envelope.Payload)
                ?? throw new InvalidOperationException(
                    $"Could not deserialize {nameof(ConversationEnded)} payload for outbox message {envelope.MessageId}.");

            await using var scope = scopeFactory.CreateAsyncScope();
            var conversations = scope.ServiceProvider.GetRequiredService<IConversationRepository>();
            var conversation = await conversations.GetByIdAsync(new ConversationId(contract.ConversationId), cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Conversation {contract.ConversationId} for ConversationEnded {envelope.MessageId} was not found.");

            var handler = scope.ServiceProvider.GetRequiredService<DispatchWebhooksForEventHandler>();
            var command = new DispatchWebhooksForConversationEvent(
                envelope.MessageId, conversation.SiteId, contract.ConversationId,
                WebhookEventTypes.ConversationClosed, contract.ClosedAt);

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
