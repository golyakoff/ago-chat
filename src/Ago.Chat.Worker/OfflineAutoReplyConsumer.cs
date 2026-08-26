using System.Text.Json;
using Ago.Chat.Application.UseCases.SendOfflineAutoReply;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Worker;

/// <summary>
/// `14-04`: the third consumer of <c>MessageAccepted</c>, alongside `2-05`'s
/// <see cref="UnreadCounterConsumer"/> and `3-02`'s <see cref="ConnectionFanoutConsumer"/>.
/// <c>Competing</c> with its own consumer name, for the reason `5-11` had to fix retroactively for the
/// other two: a shared queue would split messages between consumers instead of giving each one every
/// message.
///
/// <para>One DI scope per message - the handler's repositories are <c>Scoped</c> (they share one
/// <c>DbContext</c> per unit of work) and this class is a singleton <c>BackgroundService</c>, so it
/// cannot hold a scoped dependency in its own constructor. Same shape as the two consumers above.</para>
///
/// <para><b>A skip is an ack, not a nack.</b> Every <see cref="OfflineAutoReplyOutcome"/> except
/// <see cref="OfflineAutoReplyOutcome.Sent"/> is a correct decision not to reply - most of them are
/// the common case - so only a <c>Result</c> *failure* is thrown, and only that reaches
/// <c>RabbitMqEventConsumer</c>'s retry-then-dead-letter path (messaging.md). Retrying a message this
/// consumer correctly declined would just decline it again, more slowly.</para>
///
/// <para>Nothing here inspects the message body: <c>MessageAccepted</c> deliberately carries none, and
/// the handler reads the text from the row. This class's whole job is deserialise, scope, delegate,
/// ack.</para>
/// </summary>
public sealed class OfflineAutoReplyConsumer(
    IEventConsumer consumer,
    IServiceScopeFactory scopeFactory,
    IOptions<OfflineAutoReplyConsumerOptions> options,
    ILogger<OfflineAutoReplyConsumer> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryPolicy = new RetryPolicy(
            options.Value.MaxAttempts, options.Value.InitialBackoff, $"{SendOfflineAutoReplyHandler.ConsumerName}.dlq");

        return consumer.SubscribeAsync(
            nameof(MessageAccepted), SubscriptionMode.Competing, SendOfflineAutoReplyHandler.ConsumerName,
            retryPolicy, HandleAsync, stoppingToken);
    }

    private async Task HandleAsync(EventEnvelope envelope, IMessageContext context, CancellationToken cancellationToken)
    {
        try
        {
            var contract = JsonSerializer.Deserialize<MessageAccepted>(envelope.Payload)
                ?? throw new InvalidOperationException(
                    $"Could not deserialize {nameof(MessageAccepted)} payload for outbox message {envelope.MessageId}.");

            // A kind this build has never heard of (an older or newer producer) is not a visitor
            // message, and the safe reading of "not a visitor message" is "do not reply" - so it maps
            // to the same refusal the loop guard makes, rather than throwing and dead-lettering a
            // message the rest of the system handled fine.
            var authorKind = Enum.TryParse<MessageAuthorKind>(contract.AuthorKind, out var parsed)
                ? parsed
                : MessageAuthorKind.System;

            await using var scope = scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<SendOfflineAutoReplyHandler>();

            var command = new SendOfflineAutoReply(
                contract.MessageId,
                new SiteId(contract.SiteId),
                new ConversationId(contract.ConversationId),
                authorKind,
                contract.Sequence);

            var result = await handler.HandleAsync(command, cancellationToken);
            if (result.IsFailure)
            {
                // Thrown, not nacked directly, so the exhausted-retry dead-letter path stays the one
                // place that decision is made - UnreadCounterConsumer's own precedent.
                throw new InvalidOperationException(
                    $"{result.Error!.Value.Code}: {result.Error!.Value.Message}");
            }

            if (result.Value == OfflineAutoReplyOutcome.Sent)
            {
                logger.LogDebug(
                    "Sent an offline auto-reply in conversation {ConversationId}, triggered by message {MessageId}.",
                    contract.ConversationId, contract.MessageId);
            }

            await context.AckAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to process {MessageId} for {Consumer}.",
                envelope.MessageId, SendOfflineAutoReplyHandler.ConsumerName);
            throw;
        }
    }
}
