using System.Text.Json;
using Ago.Chat.Application.UseCases.ResolveMessageDelivery;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Worker;

/// <summary>
/// 3-02: reacts to `MessageAccepted` by resolving who should see the message live and handing off to
/// the platform's fan-out path (`ResolveMessageDeliveryTargetsHandler`). `Competing`, matching
/// `UnreadCounterConsumer` - exactly one `Worker` replica needs to resolve-and-publish per message,
/// not every replica; `Ago.Platform.Realtime`'s per-node `NodeDeliveryConsumer` is the piece that
/// genuinely needs its own dedicated topic per subscriber, not this one.
///
/// Same shape as `UnreadCounterConsumer` for the same reason: one DI scope per message (the
/// handler's repository/read-store are `Scoped`), this class itself a singleton `BackgroundService`.
/// Unlike `UnreadCounterConsumer`, a failure here is safe to retry freely - resolving participants
/// and re-publishing a fan-out has no idempotency ledger to violate, because the fan-out itself is
/// ephemeral and re-publishing it is exactly as harmless as the first publish (`adr/0020`).
/// </summary>
public sealed class ConnectionFanoutConsumer(
    IEventConsumer consumer,
    IServiceScopeFactory scopeFactory,
    IOptions<ConnectionFanoutConsumerOptions> options,
    ILogger<ConnectionFanoutConsumer> logger) : BackgroundService
{
    // `5-11`: this consumer's own stable identity - `UnreadCounterConsumer` subscribes `Competing` to
    // this same `MessageAccepted` topic too, and before this name existed the two silently shared one
    // RabbitMQ queue, splitting messages between them instead of each seeing every one.
    //
    // `15-17`: `internal`, not `private` - `Ago.Chat.Integration.Tests` (already granted
    // `InternalsVisibleTo`, `AssemblyInfo.cs`) computes this `Competing` subscription's exact queue
    // name (`{topic}.{ConsumerName}`, `RabbitMqEventConsumer`'s own naming) to poll for instead of
    // guessing at a fixed sleep - the alternative, retyping this same literal into the test, would be
    // exactly the kind of two-copies-of-one-fact drift `5-11`'s own doc comment above warns about.
    internal const string ConsumerName = "connection-fanout";

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryPolicy = new RetryPolicy(
            options.Value.MaxAttempts, options.Value.InitialBackoff, $"{ConsumerName}.dlq");

        return consumer.SubscribeAsync(
            nameof(MessageAccepted), SubscriptionMode.Competing, ConsumerName, retryPolicy, HandleAsync, stoppingToken);
    }

    private async Task HandleAsync(EventEnvelope envelope, IMessageContext context, CancellationToken cancellationToken)
    {
        try
        {
            var contract = JsonSerializer.Deserialize<MessageAccepted>(envelope.Payload)
                ?? throw new InvalidOperationException(
                    $"Could not deserialize {nameof(MessageAccepted)} payload for outbox message {envelope.MessageId}.");

            await using var scope = scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<ResolveMessageDeliveryTargetsHandler>();

            var command = new ResolveMessageDeliveryTargets(
                new ConversationId(contract.ConversationId), contract.Sequence, contract.CorrelationId);

            var result = await handler.HandleAsync(command, cancellationToken);
            if (result.IsFailure)
            {
                // Should not happen in practice: MessageAccepted is only published after the
                // message's own transaction committed (adr/0005), so the conversation it names is
                // already durable by the time this consumer sees it - same reasoning as
                // UnreadCounterConsumer's own NotFound handling.
                throw new InvalidOperationException(
                    $"{result.Error!.Value.Code}: {result.Error!.Value.Message}");
            }

            await context.AckAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to resolve message delivery for {MessageId}.", envelope.MessageId);
            throw;
        }
    }
}
