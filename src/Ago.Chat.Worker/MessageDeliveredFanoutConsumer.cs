using System.Text.Json;
using Ago.Chat.Application.UseCases.ResolveMessageDeliveredDelivery;
using Ago.Chat.Contracts;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Worker;

/// <summary>
/// `25-119`: reacts to `MessageDelivered` by pushing the delivery ack straight to the one operator
/// connection that authored the message (`ResolveMessageDeliveredTargetsHandler`). `Competing`, matching
/// `AttachmentUploadGrantFanoutConsumer`/`ConnectionFanoutConsumer` - exactly one `Worker` replica needs
/// to resolve-and-publish per event. No idempotency ledger (`adr/0020`): a purely derived, best-effort
/// notification computed from an already-outboxed event may publish directly - a redelivered
/// `MessageDelivered` just re-pushes the same, harmless notification, and the console's own next history
/// read (`GetConversationHistoryHandler`, once `MessageDto.DeliveredAt` is populated) is the backstop if
/// a push is ever missed entirely - the same "backstop is the next page load, not the only path"
/// reasoning `25-110`'s own consumer doc comment already gives.
/// </summary>
public sealed class MessageDeliveredFanoutConsumer(
    IEventConsumer consumer,
    IServiceScopeFactory scopeFactory,
    IOptions<MessageDeliveredFanoutConsumerOptions> options,
    ILogger<MessageDeliveredFanoutConsumer> logger) : BackgroundService
{
    // `5-11`: this consumer's own stable identity, required so a second Competing subscriber of
    // MessageDelivered (added later) gets its own queue instead of silently sharing this one - see
    // ConnectionFanoutConsumer's own remarks for the live bug this pattern fixes.
    //
    // `15-17`: internal, not private - see ConnectionFanoutConsumer.ConsumerName's own remarks for why a
    // test needs this exact value rather than a retyped copy of it.
    internal const string ConsumerName = "message-delivered-fanout";

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryPolicy = new RetryPolicy(
            options.Value.MaxAttempts, options.Value.InitialBackoff, $"{ConsumerName}.dlq");

        return consumer.SubscribeAsync(
            nameof(MessageDelivered), SubscriptionMode.Competing, ConsumerName, retryPolicy, HandleAsync, stoppingToken);
    }

    private async Task HandleAsync(EventEnvelope envelope, IMessageContext context, CancellationToken cancellationToken)
    {
        try
        {
            var contract = JsonSerializer.Deserialize<MessageDelivered>(envelope.Payload)
                ?? throw new InvalidOperationException(
                    $"Could not deserialize {nameof(MessageDelivered)} payload for outbox message {envelope.MessageId}.");

            await using var scope = scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<ResolveMessageDeliveredTargetsHandler>();

            var command = new ResolveMessageDeliveredTargets(
                contract.ConversationId, contract.MessageId, contract.OperatorId, contract.DeliveredAt, envelope.CorrelationId);

            var result = await handler.HandleAsync(command, cancellationToken);
            if (result.IsFailure)
            {
                throw new InvalidOperationException(
                    $"{result.Error!.Value.Code}: {result.Error!.Value.Message}");
            }

            await context.AckAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to resolve message-delivered targets for {MessageId}.", envelope.MessageId);
            throw;
        }
    }
}
