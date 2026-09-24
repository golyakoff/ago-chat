using System.Text.Json;
using Ago.Chat.Application.UseCases.NotifyOperatorDevices;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Worker;

/// <summary>
/// `26-86`: the third `Competing` subscriber alongside <see cref="OperatorAssignmentPushConsumer"/> and
/// <see cref="OperatorMessagePushConsumer"/> - a solo subscriber of its own new topic,
/// `ConversationWaitingForOperator`, rather than a third subscriber crowding onto either of the other
/// two's own topics (there is no existing publisher of this fact to piggyback on: `ConversationEnteredQueue`
/// is this item's own new domain event). Its own <see cref="ConsumerName"/> is what would keep it safe as
/// a second subscriber if one is ever added later (`5-11`'s own fix, restated for a brand-new topic that
/// happens to start with exactly one).
///
/// `NotifyOperatorDevicesHandler.HandleWaitingAsync` holds the actual decision (every non-removed
/// operator on the site who holds `conversation:read`, no message body) - this class only deserializes,
/// scopes, calls, and acks, the identical shape its two siblings already take.
/// </summary>
public sealed class OperatorWaitingPushConsumer(
    IEventConsumer consumer,
    IServiceScopeFactory scopeFactory,
    IOptions<OperatorWaitingPushConsumerOptions> options,
    ILogger<OperatorWaitingPushConsumer> logger) : BackgroundService
{
    internal const string ConsumerName = "operator-waiting-push";

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryPolicy = new RetryPolicy(
            options.Value.MaxAttempts, options.Value.InitialBackoff, $"{ConsumerName}.dlq");

        return consumer.SubscribeAsync(
            nameof(ConversationWaitingForOperator), SubscriptionMode.Competing, ConsumerName, retryPolicy, HandleAsync, stoppingToken);
    }

    private async Task HandleAsync(EventEnvelope envelope, IMessageContext context, CancellationToken cancellationToken)
    {
        try
        {
            var contract = JsonSerializer.Deserialize<ConversationWaitingForOperator>(envelope.Payload)
                ?? throw new InvalidOperationException(
                    $"Could not deserialize {nameof(ConversationWaitingForOperator)} payload for outbox message {envelope.MessageId}.");

            await using var scope = scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<NotifyOperatorDevicesHandler>();

            var command = new NotifyOperatorDeviceForWaiting(
                new ConversationId(contract.ConversationId), new SiteId(contract.SiteId), new VisitorId(contract.VisitorId));

            var result = await handler.HandleWaitingAsync(command, cancellationToken);
            if (result.IsFailure)
            {
                throw new InvalidOperationException(
                    $"{result.Error!.Value.Code}: {result.Error!.Value.Message}");
            }

            await context.AckAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The one path here that is expected to reach the DLQ rather than being swallowed:
            // `IPushSender.SendAsync` throwing means RuStore or the network itself has been unreachable
            // for the whole configured resilience window (`NotifyOperatorDevicesHandler`'s own remarks)
            // - a non-empty `operator-waiting-push.dlq` is the intended signal, the identical shape
            // `OperatorAssignmentPushConsumer`'s own remarks state for itself.
            logger.LogWarning(ex, "Failed to notify operator devices of a waiting conversation for {MessageId}.", envelope.MessageId);
            throw;
        }
    }
}
