using System.Text.Json;
using Ago.Chat.Application.UseCases.NotifyOperatorDevices;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Worker;

/// <summary>
/// `26-05`/`push-notifications.md`'s own "Fan-out" section: a new `Competing` subscriber of
/// `MessageAccepted`, alongside the topic's existing subscribers (`UnreadCounterConsumer`,
/// `ConnectionFanoutConsumer`, `OfflineAutoReplyConsumer`, `ChannelMessageDeliveryConsumer`,
/// `ModuleTaskConsumer`, `LinkIdentityCommandConsumer`) - `push-notifications.md`'s own text calls
/// this "a fifth", naming the four it counted when it was written; the topic has since grown two more
/// competing subscribers of its own, which only strengthens this consumer's own point: a per-subscriber
/// `ConsumerName` is exactly what makes adding *another* one to an already-crowded topic safe, the
/// same guarantee `5-11`'s fix gives regardless of how many siblings are already there.
/// `OperatorPushFanOutEndToEndTests` proves this live for this consumer specifically - published
/// messages reach both this consumer and an existing sibling in full, independently, never split
/// between the two.
///
/// `NotifyOperatorDevicesHandler.HandleMessageAsync` holds the actual decision (visitor-only, assigned-
/// operator-only, no message body) - this class only deserializes, scopes, calls, and acks, the
/// identical shape every sibling `MessageAccepted` consumer already takes.
/// </summary>
public sealed class OperatorMessagePushConsumer(
    IEventConsumer consumer,
    IServiceScopeFactory scopeFactory,
    IOptions<OperatorMessagePushConsumerOptions> options,
    ILogger<OperatorMessagePushConsumer> logger) : BackgroundService
{
    internal const string ConsumerName = "operator-message-push";

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
            var handler = scope.ServiceProvider.GetRequiredService<NotifyOperatorDevicesHandler>();

            var command = new NotifyOperatorDeviceForMessage(
                new ConversationId(contract.ConversationId), new MessageId(contract.MessageId), contract.AuthorKind);

            var result = await handler.HandleMessageAsync(command, cancellationToken);
            if (result.IsFailure)
            {
                // Should not happen in practice: MessageAccepted is only published after the message's
                // own transaction committed (adr/0005), so the conversation it names is already durable
                // by the time this consumer sees it - the identical reasoning every other MessageAccepted
                // consumer's own NotFound handling already gives.
                throw new InvalidOperationException(
                    $"{result.Error!.Value.Code}: {result.Error!.Value.Message}");
            }

            await context.AckAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A push send that genuinely could not reach RuStore (or the network) reaches this path -
            // see OperatorAssignmentPushConsumer's own remarks on why that is the DLQ's job, not
            // this catch's.
            logger.LogWarning(ex, "Failed to notify operator devices of a message for {MessageId}.", envelope.MessageId);
            throw;
        }
    }
}
