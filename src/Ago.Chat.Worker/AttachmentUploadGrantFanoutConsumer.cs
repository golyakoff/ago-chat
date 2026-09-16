using System.Text.Json;
using Ago.Chat.Application.UseCases.ResolveAttachmentUploadGrantDelivery;
using Ago.Chat.Contracts;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Worker;

/// <summary>
/// `25-110`: reacts to `AttachmentUploadGrantChanged` by pushing the grant/revoke to the one visitor
/// connection holding the affected conversation open (`ResolveAttachmentUploadGrantDeliveryTargetsHandler`).
/// `Competing`, matching `ConnectionFanoutConsumer`/`ConversationAssignmentFanoutConsumer` - exactly one
/// `Worker` replica needs to resolve-and-publish per event. No idempotency ledger (`adr/0020`): a purely
/// derived, best-effort notification computed from an already-outboxed event may publish directly - a
/// redelivered `AttachmentUploadGrantChanged` just re-pushes the same, harmless notification, and the
/// widget's own reconnect-riding read (`ago-widget`'s `connection.ts`) is the backstop if a push is ever
/// missed entirely.
/// </summary>
public sealed class AttachmentUploadGrantFanoutConsumer(
    IEventConsumer consumer,
    IServiceScopeFactory scopeFactory,
    IOptions<AttachmentUploadGrantFanoutConsumerOptions> options,
    ILogger<AttachmentUploadGrantFanoutConsumer> logger) : BackgroundService
{
    // `5-11`: this consumer's own stable identity, required so a second Competing subscriber of
    // AttachmentUploadGrantChanged (added later) gets its own queue instead of silently sharing this
    // one - see `ConnectionFanoutConsumer`'s own remarks for the live bug this pattern fixes.
    //
    // `15-17`: `internal`, not `private` - see `ConnectionFanoutConsumer.ConsumerName`'s own remarks
    // for why a test needs this exact value rather than a retyped copy of it.
    internal const string ConsumerName = "attachment-upload-grant-fanout";

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryPolicy = new RetryPolicy(
            options.Value.MaxAttempts, options.Value.InitialBackoff, $"{ConsumerName}.dlq");

        return consumer.SubscribeAsync(
            nameof(AttachmentUploadGrantChanged), SubscriptionMode.Competing, ConsumerName, retryPolicy, HandleAsync, stoppingToken);
    }

    private async Task HandleAsync(EventEnvelope envelope, IMessageContext context, CancellationToken cancellationToken)
    {
        try
        {
            var contract = JsonSerializer.Deserialize<AttachmentUploadGrantChanged>(envelope.Payload)
                ?? throw new InvalidOperationException(
                    $"Could not deserialize {nameof(AttachmentUploadGrantChanged)} payload for outbox message {envelope.MessageId}.");

            await using var scope = scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<ResolveAttachmentUploadGrantDeliveryTargetsHandler>();

            var command = new ResolveAttachmentUploadGrantDeliveryTargets(
                contract.ConversationId, contract.VisitorId, contract.Granted, contract.OccurredAt, envelope.CorrelationId);

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
            logger.LogWarning(ex, "Failed to resolve attachment-upload-grant delivery targets for {MessageId}.", envelope.MessageId);
            throw;
        }
    }
}
