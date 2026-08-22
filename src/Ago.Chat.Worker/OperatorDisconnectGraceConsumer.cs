using System.Text.Json;
using Ago.Chat.Application.Realtime;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Worker;

/// <summary>
/// `4-04`: reacts to `OperatorPresenceLost` (`Ago.Chat.Api`'s query-at-disconnect fast path, or
/// `OperatorDisconnectSweepJob`'s periodic backstop) by waiting `GracePeriod`, then checking
/// presence exactly once more before releasing anything. This single final check is the entire
/// "cancel a pending release on reconnect" mechanism - no per-operator timer state to track or
/// cancel: if the operator reconnected at any point before the wait ends, the registry already
/// reflects a live connection, and nothing is released. `Competing`, matching every other consumer
/// here - exactly one `Worker` replica needs to act per disconnect signal.
///
/// No idempotency ledger (`adr/0020`): a redelivered or duplicate `OperatorPresenceLost` just waits
/// and re-checks again, harmless - `ReleaseAllAsync` only ever acts on conversations still
/// genuinely `Assigned` to this operator, so a conversation already released by an earlier delivery
/// is silently skipped, not released twice.
///
/// Holding this delivery unacked for the entire `GracePeriod` (via `Task.Delay` inside the handler)
/// bounds this consumer's throughput to the broker's own prefetch count under a mass-disconnect
/// scenario (e.g. a node dying with many operators connected to it) - unmeasured, an accepted
/// trade-off at this project's scale rather than a scheduled-timer redesign (`CLAUDE.md`: "measure
/// or stay silent").
/// </summary>
public sealed class OperatorDisconnectGraceConsumer(
    IEventConsumer consumer,
    IConnectionRegistry connectionRegistry,
    OperatorConversationReleaser releaser,
    IOptions<OperatorDisconnectGraceConsumerOptions> options,
    ILogger<OperatorDisconnectGraceConsumer> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryPolicy = new RetryPolicy(
            options.Value.MaxAttempts, options.Value.InitialBackoff, "operator-disconnect-grace.dlq");

        return consumer.SubscribeAsync(
            nameof(OperatorPresenceLost), SubscriptionMode.Competing, retryPolicy, HandleAsync, stoppingToken);
    }

    private async Task HandleAsync(EventEnvelope envelope, IMessageContext context, CancellationToken cancellationToken)
    {
        try
        {
            var contract = JsonSerializer.Deserialize<OperatorPresenceLost>(envelope.Payload)
                ?? throw new InvalidOperationException(
                    $"Could not deserialize {nameof(OperatorPresenceLost)} payload for outbox message {envelope.MessageId}.");

            var operatorId = new OperatorId(contract.OperatorId);
            await Task.Delay(options.Value.GracePeriod, cancellationToken);

            var stillGone = await connectionRegistry.GetConnectionsAsync(
                PrincipalKeys.ForOperator(operatorId), cancellationToken);
            if (stillGone.Count == 0)
            {
                var released = await releaser.ReleaseAllAsync(operatorId, cancellationToken);
                if (released > 0)
                {
                    logger.LogInformation(
                        "Operator {OperatorId} had no connections for the full grace period - released {Count} conversation(s).",
                        operatorId.Value, released);
                }
            }

            await context.AckAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to process operator-disconnect grace period for {MessageId}.", envelope.MessageId);
            throw;
        }
    }
}
