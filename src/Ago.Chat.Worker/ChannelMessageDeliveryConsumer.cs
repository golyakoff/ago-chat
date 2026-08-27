using System.Text.Json;
using Ago.Chat.Application.UseCases.DeliverChannelMessage;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Worker;

/// <summary>
/// `14-02`: a fourth <c>Competing</c> consumer of <c>MessageAccepted</c>, alongside `2-05`'s
/// <see cref="UnreadCounterConsumer"/>, `3-02`'s <see cref="ConnectionFanoutConsumer"/> and `14-04`'s
/// <see cref="OfflineAutoReplyConsumer"/> - the outbound half of `14-01`'s port, proven for the first
/// time. See <see cref="DeliverChannelMessageHandler"/>'s own remarks for why a consumer, not the send
/// path, and for the loop guard that keeps this from echoing an inbound MAX message back to MAX.
///
/// <para>One DI scope per message, the same shape every consumer on this page uses -
/// <see cref="DeliverChannelMessageHandler"/> itself needs no scope (it makes no write of its own), but
/// its own dependencies (<c>IConversationRepository</c>, <c>IChannelIdentityRepository</c>) are
/// <c>Scoped</c> all the same.</para>
///
/// <para><b>A thrown exception here means the resilience pipeline's retries and circuit breaker were
/// already exhausted</b> - <c>ResilientInboundChannelAdapter</c> wraps every
/// <see cref="Application.Abstractions.IInboundChannelAdapter.SendAsync"/> call, so by the time an
/// exception reaches this consumer it is not "MAX had one slow response," it is "MAX has been
/// unreachable for the whole configured retry/breaker window." Thrown, not swallowed - the same
/// exhausted-retry dead-letter path <see cref="OfflineAutoReplyConsumer"/> already uses for its own
/// handler failures, so an operator reply that genuinely could not reach MAX is visible in the DLQ
/// rather than silently lost.</para>
/// </summary>
public sealed class ChannelMessageDeliveryConsumer(
    IEventConsumer consumer,
    IServiceScopeFactory scopeFactory,
    IOptions<ChannelMessageDeliveryConsumerOptions> options,
    ILogger<ChannelMessageDeliveryConsumer> logger) : BackgroundService
{
    public const string ConsumerName = "channel-message-delivery";

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryPolicy = new RetryPolicy(options.Value.MaxAttempts, options.Value.InitialBackoff, $"{ConsumerName}.dlq");

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

            // Same "an author kind this build has never heard of is not something to relay" reading
            // OfflineAutoReplyConsumer's own remarks apply to an unknown value here.
            var authorKind = Enum.TryParse<MessageAuthorKind>(contract.AuthorKind, out var parsed)
                ? parsed
                : MessageAuthorKind.System;

            await using var scope = scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<DeliverChannelMessageHandler>();

            var outcome = await handler.HandleAsync(
                new DeliverChannelMessage(
                    new SiteId(contract.SiteId), new ConversationId(contract.ConversationId),
                    new MessageId(contract.MessageId), authorKind, contract.Sequence),
                cancellationToken);

            if (outcome is DeliverChannelMessageOutcome.Delivered or DeliverChannelMessageOutcome.Refused)
            {
                logger.LogDebug(
                    "Channel delivery for message {MessageId} in conversation {ConversationId}: {Outcome}.",
                    contract.MessageId, contract.ConversationId, outcome);
            }

            await context.AckAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to process {MessageId} for {Consumer}.", envelope.MessageId, ConsumerName);
            throw;
        }
    }
}
