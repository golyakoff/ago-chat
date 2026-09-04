using System.Text.Json;
using Ago.Chat.Application.UseCases.ResolveConversationAssignment;
using Ago.Chat.Contracts;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Worker;

/// <summary>
/// `4-02`: reacts to `ConversationAssignedToOperator` by pushing the assignment to both
/// participants (`ResolveConversationAssignmentTargetsHandler`). `Competing`, matching
/// `ConnectionFanoutConsumer`/`UnreadCounterConsumer` - exactly one `Worker` replica needs to
/// resolve-and-publish per event. No idempotency ledger (`adr/0020`): a purely derived, best-effort
/// notification computed from an already-outboxed event may publish directly - a redelivered
/// ConversationAssignedToOperator just re-pushes the same, harmless notification.
/// </summary>
public sealed class ConversationAssignmentFanoutConsumer(
    IEventConsumer consumer,
    IServiceScopeFactory scopeFactory,
    IOptions<ConversationAssignmentFanoutConsumerOptions> options,
    ILogger<ConversationAssignmentFanoutConsumer> logger) : BackgroundService
{
    // `5-11`: this consumer's own stable identity, required so a second Competing subscriber of
    // ConversationAssignedToOperator (added later) gets its own queue instead of silently sharing
    // this one - see `ConnectionFanoutConsumer`'s own remarks for the live bug this pattern fixes.
    //
    // `15-17`: `internal`, not `private` - see `ConnectionFanoutConsumer.ConsumerName`'s own remarks
    // for why a test needs this exact value rather than a retyped copy of it.
    internal const string ConsumerName = "conversation-assignment-fanout";

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
            var handler = scope.ServiceProvider.GetRequiredService<ResolveConversationAssignmentTargetsHandler>();

            var command = new ResolveConversationAssignmentTargets(
                contract.ConversationId, contract.VisitorId, contract.OperatorId, contract.OccurredAt, contract.CorrelationId);

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
            logger.LogWarning(ex, "Failed to resolve conversation assignment targets for {MessageId}.", envelope.MessageId);
            throw;
        }
    }
}
