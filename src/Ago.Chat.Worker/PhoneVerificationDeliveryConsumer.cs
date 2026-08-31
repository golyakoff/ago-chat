using System.Text.Json;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Worker;

/// <summary>
/// `14-15`: the one caller of <see cref="IPhoneVerificationSender"/> - reacts to
/// <see cref="PhoneVerificationDeliveryRequested"/> (`InitiatePhoneVerificationHandler`'s own outbox row)
/// by placing the actual paid SMS/voice send, off the visitor-facing HTTP request entirely (CLAUDE.md
/// rule 4). `Competing`, matching every other consumer in this file - exactly one `Worker` replica needs
/// to act per pending verification. The same shape <see cref="ChannelMessageDeliveryConsumer"/>'s own
/// remarks describe for itself, restated here only where this consumer differs.
///
/// <para><b>A thrown exception here means the resilience pipeline's own retries and circuit breaker were
/// already exhausted</b> - <c>ResilientPhoneVerificationSender</c> wraps every real
/// <see cref="IPhoneVerificationSender.SendCodeAsync"/> call the same way
/// <c>ResilientInboundChannelAdapter</c> wraps <c>IInboundChannelAdapter.SendAsync</c>
/// (<see cref="ChannelMessageDeliveryConsumer"/>'s own remarks on the identical shape) - by the time an
/// exception reaches this consumer, retrying at this level has already been tried and failed. Thrown, not
/// swallowed, so a code that genuinely could not be delivered is visible in the DLQ rather than silently
/// lost - the same reasoning that handler's own remarks give.</para>
///
/// <para><b>No idempotency ledger - a redelivered message just re-sends the identical code, harmless.</b>
/// The same "no idempotency ledger" shape <c>OperatorRemovedConsumer</c>'s own remarks describe: at-least-
/// once delivery means this consumer may run twice for one <see cref="PhoneVerificationDeliveryRequested"/>,
/// and the visitor's phone simply receives the same SMS twice, or rings twice with the same digits read
/// aloud - a minor, bounded nuisance identical in kind to every other at-least-once cost this codebase
/// already accepts, never a correctness problem: the code itself does not change, and
/// <c>PendingPhoneVerification.AttemptConfirm</c> only ever consumes it once regardless of how many times
/// it was delivered.</para>
/// </summary>
public sealed class PhoneVerificationDeliveryConsumer(
    IEventConsumer consumer,
    IServiceScopeFactory scopeFactory,
    IOptions<PhoneVerificationDeliveryConsumerOptions> options,
    ILogger<PhoneVerificationDeliveryConsumer> logger) : BackgroundService
{
    public const string ConsumerName = "phone-verification-delivery";

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryPolicy = new RetryPolicy(options.Value.MaxAttempts, options.Value.InitialBackoff, $"{ConsumerName}.dlq");

        return consumer.SubscribeAsync(
            nameof(PhoneVerificationDeliveryRequested), SubscriptionMode.Competing, ConsumerName, retryPolicy,
            HandleAsync, stoppingToken);
    }

    private async Task HandleAsync(EventEnvelope envelope, IMessageContext context, CancellationToken cancellationToken)
    {
        try
        {
            var contract = JsonSerializer.Deserialize<PhoneVerificationDeliveryRequested>(envelope.Payload)
                ?? throw new InvalidOperationException(
                    $"Could not deserialize {nameof(PhoneVerificationDeliveryRequested)} payload for outbox message {envelope.MessageId}.");

            // An unknown value here would mean a future PhoneVerificationDeliveryMethod member shipped
            // without this Worker being redeployed - the same "never seen before" defensive parse
            // OfflineAutoReplyConsumer's own remarks apply to MessageAuthorKind.
            if (!Enum.TryParse<PhoneVerificationDeliveryMethod>(contract.DeliveryMethod, out var deliveryMethod))
            {
                throw new InvalidOperationException(
                    $"Unknown phone verification delivery method '{contract.DeliveryMethod}' for {envelope.MessageId}.");
            }

            await using var scope = scopeFactory.CreateAsyncScope();
            var sender = scope.ServiceProvider.GetRequiredService<IPhoneVerificationSender>();

            await sender.SendCodeAsync(
                new PhoneVerificationDelivery(contract.Phone, contract.Code, deliveryMethod), cancellationToken);

            logger.LogDebug(
                "Sent phone verification code for pending verification {PendingPhoneVerificationId} via {DeliveryMethod}.",
                contract.PendingPhoneVerificationId, deliveryMethod);

            await context.AckAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to process {MessageId} for {Consumer}.", envelope.MessageId, ConsumerName);
            throw;
        }
    }
}
