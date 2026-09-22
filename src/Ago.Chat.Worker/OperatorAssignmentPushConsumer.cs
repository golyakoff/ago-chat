using System.Text.Json;
using Ago.Chat.Application.UseCases.NotifyOperatorDevices;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Worker;

/// <summary>
/// `26-05`/`push-notifications.md`'s own "Fan-out" section: a second, independent `Competing`
/// subscriber of `ConversationAssignedToOperator`, alongside `ConversationAssignmentFanoutConsumer`
/// (4-02, the realtime push) and `ConversationAssignmentWebhookDispatchConsumer` (6-05, the tenant
/// webhook). Its own <see cref="ConsumerName"/> is what keeps it from silently sharing either
/// sibling's queue (`5-11`'s own fix, reconfirmed live for this exact shape by
/// `WebhookDispatchSharedQueueRegressionTests` and, for this new subscriber specifically, by
/// `OperatorPushFanOutEndToEndTests`).
///
/// Covers a conversation transfer for free: `ConversationTransferredMapper` maps
/// `Ago.Chat.Domain.ConversationTransferred` onto this same wire contract, so this consumer never
/// needs to know the difference between a first assignment and a transfer - both arrive as one
/// `ConversationAssignedToOperator` naming the operator to notify now.
///
/// No idempotency ledger (`adr/0020`): a purely derived, best-effort notification computed from an
/// already-outboxed event may publish directly - a redelivered `ConversationAssignedToOperator` just
/// re-sends the same, harmless push, collapsed on the client by
/// `NotifyOperatorDevicesHandler.GroupKeyFor`'s own notification tag (`push-notifications.md`'s own
/// "Idempotency, without an inbox row").
/// </summary>
public sealed class OperatorAssignmentPushConsumer(
    IEventConsumer consumer,
    IServiceScopeFactory scopeFactory,
    IOptions<OperatorAssignmentPushConsumerOptions> options,
    ILogger<OperatorAssignmentPushConsumer> logger) : BackgroundService
{
    // `5-11`: this consumer's own stable identity - see ConnectionFanoutConsumer.ConsumerName's own
    // remarks for the live bug a shared queue name would reproduce. `internal`, not `private`, for the
    // identical "a test computes this Competing subscription's exact queue name instead of retyping
    // the literal" reason that class's own remarks give.
    internal const string ConsumerName = "operator-assignment-push";

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryPolicy = new RetryPolicy(
            options.Value.MaxAttempts, options.Value.InitialBackoff, $"{ConsumerName}.dlq");

        return consumer.SubscribeAsync(
            nameof(ConversationAssignedToOperator), SubscriptionMode.Competing, ConsumerName, retryPolicy, HandleAsync, stoppingToken);
    }

    private async Task HandleAsync(EventEnvelope envelope, IMessageContext context, CancellationToken cancellationToken)
    {
        try
        {
            var contract = JsonSerializer.Deserialize<ConversationAssignedToOperator>(envelope.Payload)
                ?? throw new InvalidOperationException(
                    $"Could not deserialize {nameof(ConversationAssignedToOperator)} payload for outbox message {envelope.MessageId}.");

            await using var scope = scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<NotifyOperatorDevicesHandler>();

            var command = new NotifyOperatorDeviceForAssignment(
                new ConversationId(contract.ConversationId), new VisitorId(contract.VisitorId), new OperatorId(contract.OperatorId));

            var result = await handler.HandleAssignmentAsync(command, cancellationToken);
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
            // `IPushSender.SendAsync` throwing means RuStore or the network itself has been
            // unreachable for the whole configured resilience window (`NotifyOperatorDevicesHandler`'s
            // own remarks) - a non-empty `operator-assignment-push.dlq` is the intended signal
            // (`push-notifications.md`'s own "Quiet failure, and how it stops being quiet").
            logger.LogWarning(ex, "Failed to notify operator devices of an assignment for {MessageId}.", envelope.MessageId);
            throw;
        }
    }
}
