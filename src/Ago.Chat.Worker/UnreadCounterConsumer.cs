using System.Text.Json;
using Ago.Chat.Application.UseCases.RecordUnread;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Worker;

/// <summary>
/// 2-05's first real consumer of `MessageAccepted`: <c>Competing</c>, so any number of `Worker`
/// replicas share the load rather than each processing every message - `ConnectionFanoutConsumer`
/// (3-02) subscribes to the same topic the same way, for the same reason: resolving a message's
/// recipients and publishing the per-node fan-out only needs to happen once per message, not once
/// per `Worker` replica. One DI scope per message - the
/// handler's `IConversationRepository`/`IInboxChecker` are `Scoped` (they share one `DbContext`
/// per unit of work), and this class itself is registered as a singleton `BackgroundService`
/// (concurrency.md), so it cannot hold a scoped dependency in its own constructor.
///
/// A handler failure (including a genuinely-unexpected `Conversation.NotFound`) is raised as an
/// exception rather than an explicit `NackAsync` - `RabbitMqEventConsumer` already nacks-and-retries
/// any exception a handler throws, then dead-letters after `RetryPolicy.MaxAttempts` (messaging.md's
/// poison-message handling), so there is nothing this class needs to add.
/// </summary>
public sealed class UnreadCounterConsumer(
    IEventConsumer consumer,
    IServiceScopeFactory scopeFactory,
    IOptions<UnreadCounterConsumerOptions> options,
    ILogger<UnreadCounterConsumer> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryPolicy = new RetryPolicy(
            options.Value.MaxAttempts, options.Value.InitialBackoff, $"{RecordUnreadMessageHandler.ConsumerName}.dlq");

        return consumer.SubscribeAsync(
            nameof(MessageAccepted), SubscriptionMode.Competing, RecordUnreadMessageHandler.ConsumerName,
            retryPolicy, HandleAsync, stoppingToken);
    }

    private async Task HandleAsync(EventEnvelope envelope, IMessageContext context, CancellationToken cancellationToken)
    {
        try
        {
            var contract = JsonSerializer.Deserialize<MessageAccepted>(envelope.Payload)
                ?? throw new InvalidOperationException(
                    $"Could not deserialize {nameof(MessageAccepted)} payload for outbox message {envelope.MessageId}.");

            await using var scope = scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<RecordUnreadMessageHandler>();

            var command = new RecordUnreadMessage(
                contract.MessageId,
                new ConversationId(contract.ConversationId),
                Enum.Parse<MessageAuthorKind>(contract.AuthorKind));

            var result = await handler.HandleAsync(command, cancellationToken);
            if (result.IsFailure)
            {
                // Should not happen in practice: MessageAccepted is only published after the
                // message's own transaction committed (adr/0005), so the conversation it names is
                // already durable by the time this consumer sees it. Thrown, not nacked directly, so
                // the exhausted-retry dead-letter path is the one place this ever gets decided.
                throw new InvalidOperationException(
                    $"{result.Error!.Value.Code}: {result.Error!.Value.Message}");
            }

            if (!result.Value)
            {
                logger.LogDebug(
                    "MessageAccepted {MessageId} was already recorded for {Consumer} - redelivery, no-op.",
                    contract.MessageId, RecordUnreadMessageHandler.ConsumerName);
            }

            await context.AckAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // RabbitMqEventConsumer's own catch-and-nack-with-requeue around this delegate has no
            // logging of its own (by design - the port stays broker-generic, the adapter does not
            // know this handler's error vocabulary) - without this, a consistently-failing handler
            // dead-letters after RetryPolicy.MaxAttempts with no trace of why anywhere. Rethrown, not
            // swallowed: the retry/dead-letter decision still belongs to RabbitMqEventConsumer alone.
            logger.LogWarning(ex, "Failed to process {MessageId} for {Consumer}.",
                envelope.MessageId, RecordUnreadMessageHandler.ConsumerName);
            throw;
        }
    }
}
